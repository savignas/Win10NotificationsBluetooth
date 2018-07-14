using System;
using System.Runtime.InteropServices;

namespace Win32
{
    public sealed class Spotify
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string lpClassName,
            string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd,
            string lpString, int cch);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr hwnd,
            int msg, IntPtr wParam, IntPtr lParam);

        private const string LpClassName = "Chrome_WidgetWin_0";

        private const int APPCOMMAND_VOLUME_MUTE = 524288;
        private const int APPCOMMAND_VOLUME_DOWN = 589824;
        private const int APPCOMMAND_VOLUME_UP = 655360;
        private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 917504;
        private const int APPCOMMAND_MEDIA_PAUSE = 290816;
        private const int APPCOMMAND_MEDIA_NEXTTRACK = 720896;
        private const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 786432;
        private const int WM_APPCOMMAND = 0x319;

        public static string GetSongTitle()
        {
            var hwnd = FindWindowW(LpClassName, null);

            if (hwnd.Equals(IntPtr.Zero)) return null;

            var lpText = new string((char)0, 100);
            var intLength = GetWindowText(hwnd, lpText, lpText.Length);

            if (intLength <= 0 || intLength > lpText.Length)
                return "unknown";

            var strTitle = lpText.Substring(0, intLength);

            if (strTitle == "Spotify")
            {
                strTitle = "Not Playing";
            }

            return strTitle.Trim();
        }

        private static bool IsNumeric(string value)
        {
            try
            {
                return double.TryParse(value, out var _);
            }
            catch
            {
                return false;
            }
        }

        private static void Command(int cmd)
        {
            var hwnd = FindWindowW(LpClassName, null);
            SendMessageW(hwnd, WM_APPCOMMAND, (IntPtr)0, (IntPtr)cmd);
        }

        public static void NextTrack()
        {
            Command(APPCOMMAND_MEDIA_NEXTTRACK);
        }

        public static void Stop()
        {
            Command(APPCOMMAND_MEDIA_PAUSE);
        }

        public static void Pause()
        {
            Command(APPCOMMAND_MEDIA_PAUSE);
        }

        public static void Play()
        {
            Command(APPCOMMAND_MEDIA_PLAY_PAUSE);
        }

        public static void PreviousTrack()
        {
            Command(APPCOMMAND_MEDIA_PREVIOUSTRACK);
        }

        public static void Mute()
        {
            Command(APPCOMMAND_VOLUME_MUTE);
        }
    }
}
