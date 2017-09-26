using System;
using System.Runtime.InteropServices;

namespace Win32
{
    public sealed class Spotify
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName,
            string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd,
            string lpString, int cch);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr hwnd,
            int msg, int wParam, int lParam);

        private const string LpClassName = "SpotifyMainWindow";

        private const int WmAppCommand = 0x0319;
        private const int CmdPrevious = 786432;
        private const int CmdPlayPause = 917504;
        private const int CmdStop = 851968;
        private const int CmdNext = 720896;

        public static string GetSongTitle()
        {
            var hwnd = FindWindow(LpClassName, null);

            if (hwnd.Equals(IntPtr.Zero)) return null;

            var lpText = new string((char)0, 120);
            var intLength = GetWindowText(hwnd, lpText, lpText.Length);

            if (intLength <= 0 || intLength > lpText.Length)
                return "unknown";

            var strTitle = lpText.Substring(0, intLength);

            if (strTitle == "" || strTitle == "Spotify")
            {
                strTitle = "Not Playing";
            }

            return strTitle;
        }

        private static void Command(int cmd)
        {
            var hwnd = FindWindow(LpClassName, null);
            SendMessageW(hwnd, WmAppCommand, 0, cmd);
        }

        public static void NextTrack()
        {
            Command(CmdNext);
        }

        public static void Stop()
        {
            Command(CmdStop);
        }

        public static void Pause()
        {
            Command(CmdPlayPause);
        }

        public static void Play()
        {
            Command(CmdPlayPause);
        }

        public static void PreviousTrack()
        {
            Command(CmdPrevious);
        }
    }
}
