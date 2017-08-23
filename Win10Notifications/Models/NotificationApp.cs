using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
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

        public async Task<byte[]> Serialize(RandomAccessStreamReference icon)
        {
            using (var m = new MemoryStream())
            {
                using (var writer = new BinaryWriter(m))
                {
                    writer.Write(Key);
                    writer.Write(Name);
                    writer.Write(Value);

                    var randomAccessStream = await icon.OpenReadAsync();
                    var stream = randomAccessStream.AsStreamForRead();
                    var buffer = new byte[stream.Length];
                    await stream.ReadAsync(buffer, 0, buffer.Length);

                    writer.Write(buffer.Length);
                    writer.Write(buffer);
                }
                return m.ToArray();
            }
        }

        public static async Task<NotificationApp> Deserialize(byte[] data)
        {
            using (var m = new MemoryStream(data))
            {
                using (var reader = new BinaryReader(m))
                {
                    var notificationApp = new NotificationApp
                    {
                        Key = reader.ReadString(),
                        Name = reader.ReadString(),
                        Value = reader.ReadBoolean()
                    };

                    var lenght = reader.ReadInt32();
                    var buffer = reader.ReadBytes(lenght);
                    var stream = new MemoryStream(buffer).AsRandomAccessStream();
                    var icon = new BitmapImage();
                    await icon.SetSourceAsync(stream);

                    notificationApp.Icon = icon;

                    return notificationApp;
                }
            }
        }
    }
}
