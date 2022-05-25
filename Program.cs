using MQTTnet;
using MQTTnet.Adapter;
using MQTTnet.Client;
using MQTTnet.Client.Connecting;
using MQTTnet.Client.Options;
using MQTTnet.Client.Publishing;
using MQTTnet.Packets;
using Opc.Ua;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace OpcUaPubSub
{
    public class Program
    {
        private static IMqttClient _client = null;
        private static string _clientName = "erich";

        public static void Connect()
        {
            // disconnect if still connected
            if ((_client != null) && _client.IsConnected)
            {
                _client.DisconnectAsync().GetAwaiter().GetResult();
                _client.Dispose();
                _client = null;
            }

            string brokerName = "OSDU.azure-devices.net";
            string sharedKey = "";
            string userName = brokerName + "/" + _clientName + "/?api-version=2018-06-30";

            // create SAS token as password
            TimeSpan sinceEpoch = DateTime.UtcNow - new DateTime(1970, 1, 1);
            int week = 60 * 60 * 24 * 7;
            string expiry = Convert.ToString((int)sinceEpoch.TotalSeconds + week);
            string stringToSign = HttpUtility.UrlEncode(brokerName + "/devices/" + _clientName) + "\n" + expiry;
            HMACSHA256 hmac = new HMACSHA256(Convert.FromBase64String(sharedKey));
            string signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
            string password = "SharedAccessSignature sr=" + HttpUtility.UrlEncode(brokerName + "/devices/" + _clientName) + "&sig=" + HttpUtility.UrlEncode(signature) + "&se=" + expiry;

            // create MQTTv3 client
            _client = new MqttFactory().CreateMqttClient();
            var clientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(opt => opt.NoDelay = true)
                .WithClientId(_clientName)
                .WithTcpServer(brokerName, 8883)
                .WithTls(new MqttClientOptionsBuilderTlsParameters { UseTls = true })
                .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311)
                .WithCommunicationTimeout(TimeSpan.FromSeconds(10))
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(100))
                .WithCleanSession(true) // clear existing subscriptions 
                .WithCredentials(userName, password);

            // setup disconnection handling
            _client.UseDisconnectedHandler(disconnectArgs =>
            {
                Console.WriteLine($"Disconnected from MQTT broker: {disconnectArgs.Reason}");

                // wait a 5 seconds, then simply reconnect again, if needed
                Task.Delay(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                if ((_client == null) || !_client.IsConnected)
                {
                    Connect();
                }
            });

            try
            {
                var connectResult = _client.ConnectAsync(clientOptions.Build(), CancellationToken.None).GetAwaiter().GetResult();
                if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
                {
                    var status = GetStatus(connectResult.UserProperties)?.ToString("x4");
                    throw new Exception($"Connect failed. Status: {connectResult.ResultCode}; status: {status}");
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
        }

        public static void Main()
        { 
            // load data
            List<string> lines = File.ReadLines(Path.Combine(Directory.GetCurrentDirectory(), "energy.csv")).ToList();

            // connect to broker
            Connect();
            
            try
            {
                int i = 0;
                double energyMeter = 0;
                while (true)
                {
                    if (i == lines.Count)
                    {
                        i = 0;
                    }

                    // our energy CSV is in 30 second internvals
                    double currentPowerConsumption = double.Parse(lines[i]) / 30;
                    energyMeter += currentPowerConsumption;

                    // OPC UA PubSub JSON-encode data read
                    JsonEncoder encoder = new JsonEncoder(ServiceMessageContext.GlobalContext, true);
                    encoder.WriteString("MessageId", i++.ToString());
                    encoder.WriteString("MessageType", "ua-data");
                    encoder.WriteString("PublisherId", "MQTTPublisherMVP");
                    encoder.PushArray("Messages");
                    encoder.PushStructure("");
                    encoder.WriteString("DataSetWriterId", "12345");
                    encoder.WriteString("Timestamp", String.Format("{0:u}", DateTime.UtcNow));
                    encoder.PushStructure("Payload");
                    encoder.WriteVariant("Energy", energyMeter);
                    encoder.PopStructure();
                    encoder.PopStructure();
                    encoder.PopArray();
                    string payload = encoder.CloseAndReturnText();

                    // send to MQTTv3 broker
                    var message = new MqttApplicationMessageBuilder()
                        .WithAtLeastOnceQoS()
                        .WithTopic("devices/" + _clientName + "/messages/events/")
                        .WithContentType("application/json") // optional: sets `content-type` system property on message
                        .WithPayload(Encoding.UTF8.GetBytes(payload))
                        .Build();

                    var response = _client.PublishAsync(message).GetAwaiter().GetResult();
                    if (response.ReasonCode != MqttClientPublishReasonCode.Success)
                    {
                        throw new Exception($"Failed to send telemetry event. Reason Code: {response.ReasonCode}; Status: {GetStatus(response.UserProperties)?.ToString("x4") ?? "-"}");
                    }

                    // publish once a second
                    Task.Delay(1000).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception: " + ex.Message);

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
    }
}
