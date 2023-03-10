using Confluent.Kafka;
using Opc.Ua;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpcUaPubSub
{
    public class Program
    {
        private static IProducer<Null, string> _producer = null;

        public static void Connect()
        {
            try
            {
                // disconnect if still connected
                if (_producer != null)
                {
                    _producer.Flush();
                    _producer.Dispose();
                    _producer = null;
                }

                // create Kafka client
                var config = new ProducerConfig
                {
                    BootstrapServers = "LNIAAS.servicebus.windows.net:9093",
                    MessageTimeoutMs = 10000,
                    SecurityProtocol = SecurityProtocol.SaslSsl,
                    SaslMechanism = SaslMechanism.Plain,
                    SaslUsername = "$ConnectionString",
                    SaslPassword = ""
                };

                _producer = new ProducerBuilder<Null, string>(config).Build();

                Console.WriteLine("Connected to Kafka broker.");

            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to connect to Kafka broker: " + ex.Message);
            }
        }

        public static void Main()
        {
            // load our CSV data
            List<string> lines = File.ReadLines(Path.Combine(Directory.GetCurrentDirectory(), "energy2.csv")).ToList();

            // connect to broker
            Connect();

            try
            {
                int i = 0;
                double energyMeter = 0;
                double currentEnergyConsumption = 0;
                while (true)
                {
                    if (i == lines.Count)
                    {
                        i = 0;
                    }

                    try
                    {
                        currentEnergyConsumption = double.Parse(lines[i]);
                    }
                    catch (Exception)
                    {
                        // do nothing
                    }

                    energyMeter += currentEnergyConsumption;

                    // OPC UA PubSub JSON-encode data read
                    JsonEncoder encoder = new JsonEncoder(ServiceMessageContext.GlobalContext, true);
                    encoder.WriteString("MessageId", i.ToString());
                    encoder.WriteString("MessageType", "ua-data");
                    encoder.WriteString("PublisherId", "Festo");
                    encoder.PushArray("Messages");
                    encoder.PushStructure("");
                    encoder.WriteString("DataSetWriterId", "12345");
                    encoder.WriteString("Timestamp", string.Format("{0:u}", DateTime.UtcNow));
                    encoder.PushStructure("Payload");
                    encoder.WriteVariant("Energy", energyMeter);
                    encoder.PopStructure();
                    encoder.PopStructure();
                    encoder.PopArray();

                    Message<Null, string> message = new()
                    {
                        Headers = new Headers() { { "Content-Type", Encoding.UTF8.GetBytes("application/json") } },
                        Value = encoder.CloseAndReturnText()
                    };

                    _producer.ProduceAsync("festo", message).GetAwaiter().GetResult();

                    // publish once a second
                    Task.Delay(1000).GetAwaiter().GetResult();

                    i++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception: " + ex.Message);

                _producer.Flush();
                _producer.Dispose();
            }
        }
    }
}
