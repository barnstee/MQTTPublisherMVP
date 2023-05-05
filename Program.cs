using MQTTnet;
using MQTTnet.Adapter;
using MQTTnet.Client;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Client.ComplexTypes;
using Opc.Ua.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpcUaPubSub
{
    public class Program
    {
        private static IMqttClient _client = null;

        public static void Main()
        {
            // create OPC UA client app
            ApplicationInstance app = new ApplicationInstance
            {
                ApplicationName = "MQTTPublisher",
                ApplicationType = ApplicationType.Client,
                ConfigSectionName = "Mqtt.Publisher"
            };

            app.LoadApplicationConfiguration(false).GetAwaiter().GetResult();
            app.CheckApplicationInstanceCertificate(false, 0).GetAwaiter().GetResult();

            // create OPC UA cert validator
            app.ApplicationConfiguration.CertificateValidator = new CertificateValidator();
            app.ApplicationConfiguration.CertificateValidator.CertificateValidation += new CertificateValidationEventHandler(OPCUAServerCertificateValidationCallback);

            string brokerName = "<TODO: Enter your Azure IoT Hub hostname in here, i.e. something.azure-devices.net";
            string clientName = "<TODO: Enter your Azure IoT device ID in here>";
            string sharedKey = "<TODO: Enter your Azure IoT device's primary key in here>";

            // create SAS token
            var at = DateTimeOffset.UtcNow;
            var atString = at.ToUnixTimeMilliseconds().ToString();
            var expiry = at.AddMinutes(40);
            var expiryString = expiry.ToUnixTimeMilliseconds().ToString();
            string toSign = $"{brokerName}\n{clientName}\n{""}\n{atString}\n{expiryString}\n";
            var hmac = new HMACSHA256(Convert.FromBase64String(sharedKey));
            var sas = hmac.ComputeHash(Encoding.UTF8.GetBytes(toSign));

            // create MQTTv5 client
            _client = new MqttFactory().CreateMqttClient();
            _client.ApplicationMessageReceivedAsync += msg => HandleMessageAsync(msg);

            var clientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(opt => opt.NoDelay = true)
                .WithClientId(clientName)
                .WithTcpServer(brokerName, 8883)
                .WithTls(new MqttClientOptionsBuilderTlsParameters { UseTls = true })
                .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
                .WithUserProperty("host", brokerName) // normally it is not needed as SNI is added by most TLS implementations.
                .WithUserProperty("api-version", "2020-10-01-preview")
                .WithTimeout(TimeSpan.FromSeconds(30))
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(300))
                .WithCleanSession(false) // keep existing subscriptions
                .WithAuthentication("SAS", sas)
                .WithUserProperty("sas-at", atString)
                .WithUserProperty("sas-expiry", expiryString);

            // setup disconnection handling: print out details and allow process to close
            bool disconnected = false;
            _client.DisconnectedAsync += disconnectArgs =>
            {
                Console.WriteLine($"Disconnected: {disconnectArgs.Reason}");
                disconnected = true;

                return Task.CompletedTask;
            };

            try
            {
                var connectResult = _client.ConnectAsync(clientOptions.Build(), CancellationToken.None).GetAwaiter().GetResult();
                if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
                {
                    var status = GetStatus(connectResult.UserProperties)?.ToString("x4");
                    throw new Exception($"Connect failed. Status: {connectResult.ResultCode}; status: {status}");
                }

                var subscribeResult = _client.SubscribeAsync(
                    new MqttTopicFilter
                    {
                        Topic = "$iothub/methods/+",
                        QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce
                    }).GetAwaiter().GetResult();

                // make sure subscriptions were successful
                if (subscribeResult.Items.Count != 1 || subscribeResult.Items.ElementAt(0).ResultCode != MqttClientSubscribeResultCode.GrantedQoS0)
                {
                    throw new ApplicationException("Failed to subscribe");
                }
            }
            catch (MqttConnectingFailedException ex)
            {
                Console.WriteLine($"Failed to connect, reason code: {ex.ResultCode}");
                if (ex.Result?.UserProperties != null)
                {
                    foreach (var prop in ex.Result.UserProperties)
                    {
                        Console.WriteLine($"{prop.Name}: {prop.Value}");
                    }
                }
            }

            // find endpoint on a local OPC UA server
            string serverEndpoint = "opc.tcp://localhost:50000"; // run this sample OPC UA server locally via: docker run -p 50000:50000 mcr.microsoft.com/iotedge/opc-plc --aa --ctb
            EndpointDescription endpointDescription = CoreClientUtils.SelectEndpoint(serverEndpoint, false);
            EndpointConfiguration endpointConfiguration = EndpointConfiguration.Create(app.ApplicationConfiguration);
            ConfiguredEndpoint endpoint = new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);

            // Create OPC UA session
            Session session = Session.Create(app.ApplicationConfiguration, endpoint, false, false, app.ApplicationConfiguration.ApplicationName, 30 * 60 * 1000, new UserIdentity(), null).GetAwaiter().GetResult();
            if (!session.Connected)
            {
                Console.WriteLine("Connection to OPC UA server failed!");
                return;
            }

            // load complex type system
            ComplexTypeSystem complexTypeSystem = new ComplexTypeSystem(session);

            // send data for a minute, every second
            try
            {
                int i = 0;
                while (!disconnected)
                {
                    int publishingInterval = 1000;

                    // read a variable node from the OPC UA server (for example a variable node based on a complex type, contained in the sample OPC PLC provided by Microsoft)
                    ExpandedNodeId nodeID = ExpandedNodeId.Parse("nsu=http://microsoft.com/Opc/OpcPlc/Boiler;i=15013");
                    VariableNode node = (VariableNode)session.ReadNode(ExpandedNodeId.ToNodeId(nodeID, session.NamespaceUris));

                    ExpandedNodeId nodeTypeId = node.DataType;
                    complexTypeSystem.LoadType(nodeTypeId).GetAwaiter().GetResult();

                    // now that we have loaded the complex type, we can read the value
                    DataValue value = session.ReadValue(ExpandedNodeId.ToNodeId(nodeID, session.NamespaceUris));

                    // OPC UA PubSub JSON-encode data read
                    JsonEncoder encoder = new JsonEncoder(session.MessageContext, true);
                    encoder.WriteString("MessageId", i++.ToString());
                    encoder.WriteString("MessageType", "ua-data");
                    encoder.WriteString("PublisherId", app.ApplicationName);
                    encoder.PushArray("Messages");
                    encoder.PushStructure("");
                    encoder.WriteString("DataSetWriterId", endpointDescription.Server.ApplicationUri + ":" + publishingInterval.ToString());
                    encoder.PushStructure("Payload");
                    encoder.WriteDataValue(node.DisplayName.ToString(), value);
                    encoder.PopStructure();
                    encoder.PopStructure();
                    encoder.PopArray();
                    string payload = encoder.CloseAndReturnText();

                    // send to MQTTv5 broker
                    var message = new MqttApplicationMessageBuilder()
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithTopic("$iothub/telemetry")
                        .WithContentType("application/json") // optional: sets `content-type` system property on message
                        .WithUserProperty("@myProperty", "my value") // optional: adds custom property `myProperty`
                        .WithUserProperty("creation-time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()) // optional: sets `creation-time` system property on message
                        .WithPayload(Encoding.UTF8.GetBytes(payload))
                        .Build();

                    var response = _client.PublishAsync(message).GetAwaiter().GetResult();

                    Task.Delay(publishingInterval).GetAwaiter().GetResult();
                }

                Console.WriteLine("Exciting!");

                session.Close();
                session.Dispose();

                _client.DisconnectAsync().GetAwaiter().GetResult();
                _client.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception: " + ex.Message);

                session.Close();
                session.Dispose();

                _client.DisconnectAsync().GetAwaiter().GetResult();
                _client.Dispose();
            }
        }

        // parses status from packet properties
        private static int? GetStatus(List<MqttUserProperty> properties)
        {
            var status = properties.FirstOrDefault(up => up.Name == "status");
            if (status == null)
            {
                return null;
            }

            return int.Parse(status.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        // handles all incoming messages
        private static Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            var msg = args.ApplicationMessage;
            if (msg.Topic.StartsWith("$iothub/methods/"))
            {
                var fork = Task.Run(async () =>
                {
                    var response = HandleMethod(msg);
                    await _client.PublishAsync(response).ConfigureAwait(false);
                });
            }
            else
            {
                Console.WriteLine("Unknown topic received: " + msg.Topic);
            }

            return Task.CompletedTask;
        }

        // handles direct method calls
        private static MqttApplicationMessage HandleMethod(MqttApplicationMessage message)
        {
            Console.WriteLine($"Received method call:\ntopic:{message.Topic}\npayload as a string: {Encoding.UTF8.GetString(message.PayloadSegment)}");
            return new MqttApplicationMessageBuilder()
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .WithTopic("$iothub/responses")
                .WithCorrelationData(message.CorrelationData)
                .WithUserProperty("response-code", "200")
                .WithPayload("{\"test\":123}")
                .Build();
        }

        private static void OPCUAServerCertificateValidationCallback(CertificateValidator validator, CertificateValidationEventArgs e)
        {
            // always trust the OPC UA server certificate
            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted)
            {
                e.Accept = true;
            }
        }
    }
}
