using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media.Imaging;

namespace Win10Notifications.Models
{
    public class NotificationApp
    {
        public NotificationApp()
        {
        }

        public NotificationApp(string key, string name, bool value)
        {
            Key = key;
            Name = name;
            Value = value;
        }

        public string Key { get; set; }

        public string Name { get; set; }

        public bool Value { get; set; }

        public BitmapImage Icon { get; set; }

        public byte[] IconData { get; set; }

        public bool Delete { get; set; }

        public byte[] Serialize()
        {
            using (var m = new MemoryStream())
            {
                using (var writer = new BinaryWriter(m))
                {
                    writer.Write(Key);
                    writer.Write(Name);
                    writer.Write(Value);
                    writer.Write(IconData.Length);
                    writer.Write(IconData);
                }
                return m.ToArray();
            }
        }

        public static async Task<List<NotificationApp>> Deserialize(byte[] data)
        {
            using (var m = new MemoryStream(data))
            {
                using (var reader = new BinaryReader(m))
                {
                    var notificationApps = new List<NotificationApp>();

                    while (true)
                    {
                        try
                        {
                            var notificationApp = new NotificationApp
                            {
                                Key = reader.ReadString(),
                                Name = reader.ReadString(),
                                Value = reader.ReadBoolean()
                            };

                            var iconLenght = reader.ReadInt32();
                            if (iconLenght != 0)
                            {
                                var buffer = reader.ReadBytes(iconLenght);
                                notificationApp.IconData = buffer;
                                var stream = new MemoryStream(buffer).AsRandomAccessStream();
                                var icon = new BitmapImage();
                                await icon.SetSourceAsync(stream);

                                notificationApp.Icon = icon;
                            }
                            
                            notificationApps.Add(notificationApp);
                        }
                        catch (Exception)
                        {
                            break;
                        }
                    }

                    return notificationApps;
                }
            }
        }
    }
}
