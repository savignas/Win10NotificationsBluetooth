using System.IO;
using Windows.UI.Xaml.Media.Imaging;

namespace Tasks.Models
{
    public sealed class NotificationApp
    {
        public NotificationApp()
        {
        }

        public NotificationApp(string key, string name, bool allowed)
        {
            Key = key;
            Name = name;
            Allowed = allowed;
        }

        public string Key { get; set; }

        public string Name { get; set; }

        public bool Allowed { get; set; }

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
                    writer.Write(Allowed);
                    writer.Write(IconData.Length);
                    writer.Write(IconData);
                }
                return m.ToArray();
            }
        }
    }
}
