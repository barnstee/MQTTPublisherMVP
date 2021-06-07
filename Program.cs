using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Client.ComplexTypes;
using Opc.Ua.Configuration;
using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace OpcUaPubSub
{
    public class Program
    {
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

            // create MQTT client
            string brokerName = "";
            string clientName = "";
            string sharedKey = "";
            string userName = brokerName + "/" + clientName + "/?api-version=2018-06-30";
            MqttClient mqttClient = new MqttClient(brokerName, 8883, true, MqttSslProtocols.TLSv1_2, MQTTBrokerCertificateValidationCallback, null);

            // create SAS token
            TimeSpan sinceEpoch = DateTime.UtcNow - new DateTime(1970, 1, 1);
            int week = 60 * 60 * 24 * 7;
            string expiry = Convert.ToString((int)sinceEpoch.TotalSeconds + week);
            string stringToSign = HttpUtility.UrlEncode(brokerName + "/devices/" + clientName) + "\n" + expiry;
            HMACSHA256 hmac = new HMACSHA256(Convert.FromBase64String(sharedKey));
            string signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
            string password = "SharedAccessSignature sr=" + HttpUtility.UrlEncode(brokerName + "/devices/" + clientName) + "&sig=" + HttpUtility.UrlEncode(signature) + "&se=" + expiry;

            // connect to MQTT broker
            byte returnCode = mqttClient.Connect(clientName, userName, password);
            if (returnCode != MqttMsgConnack.CONN_ACCEPTED)
            {
                Console.WriteLine("Connection to MQTT broker failed with " + returnCode.ToString() + "!");
                return;
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
            for (int i = 0; i < 60; i++)
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
                encoder.WriteString("MessageId", i.ToString());
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

                // send to MQTT broker
                string topic = "devices/" + clientName + "/messages/events/";
                ushort result = mqttClient.Publish(topic, Encoding.UTF8.GetBytes(payload), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, false);

                Task.Delay(publishingInterval).GetAwaiter().GetResult();
            }

            session.Close();
            session.Dispose();
            mqttClient.Disconnect();
        }

        private static void OPCUAServerCertificateValidationCallback(CertificateValidator validator, CertificateValidationEventArgs e)
        {
            // always trust the OPC UA server certificate
            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted)
            {
                e.Accept = true;
            }
        }

        private static bool MQTTBrokerCertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            // always trust the MQTT broker certificate
            return true;
        }
    }
}
