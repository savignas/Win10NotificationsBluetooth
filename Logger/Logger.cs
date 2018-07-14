using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace Logger
{
    public class Logger
    {
        private static readonly StorageFolder LocalFolder =
            ApplicationData.Current.LocalFolder;

        private static StorageFile _logFile;

        public static async Task CreateLogFile()
        {
            _logFile = await LocalFolder.CreateFileAsync("log.txt", CreationCollisionOption.OpenIfExists);
        }

        private static async void WriteLog(string content)
        {
            await FileIO.AppendTextAsync(_logFile, DateTime.Now + "|" + content + '\n');
        }

        public static void Info(string content)
        {
            WriteLog("I|" + content);
        }

        public static void Error(string content, Exception exception)
        {
            WriteLog("E|" + content + "|" + exception);
        }
    }
}
