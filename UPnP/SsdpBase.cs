using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace UPnP
{
    /// <summary>
    /// Base class for Simple Service Discovery Protocol implementation
    /// </summary>
    /// <typeparam name="TUdpSocket">UDP socket type</typeparam>
    /// <typeparam name="TAddress">IP address type</typeparam>
    public abstract class SsdpBase<TUdpSocket, TAddress> : ISsdp
        where TUdpSocket : IDisposable, IUdpSocket<TAddress>, new()
    {
        private readonly int UPnPMulticastPort = 1900;

        /// <summary>
        /// Reception timeout
        /// </summary>
        protected readonly int ReceptionTimeout = 3000;

        private IDictionary<AddressType, string> MulticastAddresses { get; } =
            new Dictionary<AddressType, string>
            {
                [AddressType.IPv4] = "239.255.255.250",
                [AddressType.IPv6LinkLocal] = "FF02::C",
                [AddressType.IPv6SiteLocal] = "FF05::C",
            };

        /// <summary>
        /// Gets the type of the IP address
        /// </summary>
        /// <param name="address">IP address</param>
        /// <returns>IP adress type</returns>
        protected abstract AddressType GetAddressType(TAddress address);

        /// <summary>
        /// Gets a collection of local IP addresses
        /// </summary>
        /// <returns>a collection of local IP addresses</returns>
        protected abstract IEnumerable<TAddress> GetLocalAddresses();

        private async Task<IEnumerable<string>> GetDevices(string deviceType)
        {
            var tasks = new List<Task<IEnumerable<string>>>();
            foreach (var localAddress in GetLocalAddresses())
            {
                tasks.Add(SearchDevices(localAddress, deviceType));
            }
            var results = await Task.WhenAll(tasks);
            return results.SelectMany(result => result);
        }

        /// <summary>
        /// Add a response to the collection of response
        /// </summary>
        /// <param name="responses">the collection of response</param>
        /// <param name="buffer">data to add</param>
        /// <param name="length">length of the data (or null to add the complete data)</param>
        private void AddResponse(ICollection<string> responses, byte[] buffer, int? length = null)
        {
            if (length > 0 || length == null)
            {
                responses.Add(Encoding.UTF8.GetString(buffer, 0, length ?? buffer.Length));
            }
        }

        private async Task<IEnumerable<string>> SearchDevices(TAddress localAddress, string deviceType)
        {
            var responses = new List<string>();

            var addressType = GetAddressType(localAddress);
            if (addressType != AddressType.Unknown)
            {
                try
                {
                    using (var udpSocket = new TUdpSocket())
                    {
                        udpSocket.MessageReceived += (sender, e) =>
                            {
                                AddResponse(responses, e.Message, e.Length);
                            };
                        await udpSocket.BindAsync(localAddress);

                        var multicastAddress = MulticastAddresses[addressType];
                        var req = "M-SEARCH * HTTP/1.1\r\n" +
                            $"HOST: {multicastAddress}:{UPnPMulticastPort}\r\n" +
                            $"ST: {deviceType}\r\n" +
                            "MAN: \"ssdp:discover\"\r\n" +
                            "MX: 3\r\n\r\n";
                        var data = Encoding.UTF8.GetBytes(req);
                        for (int i = 0; i < 3; i++)
                        {
                            await udpSocket.SendToAsync(multicastAddress, UPnPMulticastPort, data);
                        }

                        await Task.Delay(ReceptionTimeout);
                    }
                }
                catch (TimeoutException) { }
                catch (ObjectDisposedException) { }
            }

            return responses;
        }

        private Dictionary<string, string> GetProperties(string response)
        {
            var properties = new Dictionary<string, string>();
            foreach (var x in response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (x.Contains(":"))
                {
                    var strings = x.Split(':');
                    properties.Add(strings[0].ToLower(), x.Remove(0, strings[0].Length + 1));
                }
            }
            return properties;
        }

        /// <summary>
        /// Search devices
        /// </summary>
        /// <param name="deviceType">device type</param>
        /// <returns>a collection of notifications</returns>

        public async Task<IEnumerable<DeviceNotification>> SearchDevices(string deviceType)
        {
            var responses = await GetDevices(deviceType);

            var devices = new List<DeviceNotification>();
            DeviceNotification deviceNotification;
            var xmlSerializer = new XmlSerializer(typeof(DeviceNotification));
            var deviceNotificationProperties = typeof(DeviceNotification).GetRuntimeProperties();
            foreach (var response in responses)
            {
                var properties = GetProperties(response);
                deviceNotification = new DeviceNotification();
                foreach (var property in deviceNotificationProperties)
                {
                    property.SetValue(deviceNotification, properties[property.Name.ToLower()], null);
                }
                if (!devices.Any(dn => dn.USN == deviceNotification.USN))
                {
                    devices.Add(deviceNotification);
                }
            }
            return devices;
        }

        /// <summary>
        /// Search UPnP devices
        /// </summary>
        /// <param name="deviceType">device type</param>
        /// <param name="deviceVersion">device version</param>
        /// <returns>a collection of found devices</returns>
        public async Task<IEnumerable<Device>> SearchUPnPDevices(string deviceType, int deviceVersion = 1)
        {
            var devices = new List<Device>();
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.ExpectContinue = false;
            var xmlSerializer = new XmlSerializer(typeof(Device));
            foreach (var deviceNotification in await SearchDevices($"urn:schemas-upnp-org:device:{deviceType}:{deviceVersion}"))
            {
                try
                {
                    var response = await httpClient.GetByteArrayAsync(deviceNotification.Location);
                    using (var textReader = new StringReader(Encoding.UTF8.GetString(response, 0, response.Length)))
                    {
                        using (var xmlReader = XmlReader.Create(textReader))
                        {
                            string urlBase = null;
                            while (xmlReader.Read())
                            {
                                if (xmlReader.Name == "URLBase")
                                {
                                    urlBase = xmlReader.ReadElementContentAsString();
                                }
                                if (xmlReader.Name == "device")
                                {
                                    var device = (Device)xmlSerializer.Deserialize(xmlReader.ReadSubtree());
                                    device.URLBase = urlBase ?? deviceNotification.Location;
                                    devices.Add(device);
                                }
                            }
                        }
                    }
                }
                catch (Exception) { }
            }
            return devices;
        }
    }
}
