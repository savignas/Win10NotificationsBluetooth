using System;
using System.Runtime.InteropServices;

namespace Win32
{
    public sealed class Winamp
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

        private const string LpClassName = "Winamp v1.x";
        private const string StrTtlEnd = " - Winamp";

        private const int WmCommand = 0x111;
        private const int WaPrevtrack = 40044;
        private const int WaPlay = 40045;
        private const int WaPause = 40046;
        private const int WaStop = 40047;
        private const int WaNexttrack = 40048;

        public static string GetSongTitle()
        {
            var hwnd = FindWindow(LpClassName, null);

            if (hwnd.Equals(IntPtr.Zero)) return null;

            var lpText = new string((char)0, 100);
            var intLength = GetWindowText(hwnd, lpText, lpText.Length);

            if (intLength <= 0 || intLength > lpText.Length)
                return "unknown";

            var strTitle = lpText.Substring(0, intLength);
            var intName = strTitle.IndexOf(StrTtlEnd, StringComparison.Ordinal);
            var intLeft = strTitle.IndexOf("[", StringComparison.Ordinal);
            var intRight = strTitle.IndexOf("]", StringComparison.Ordinal);

            if (intName >= 0 && intLeft >= 0 && intName < intLeft &&
                intRight >= 0 && intLeft + 1 < intRight)
                strTitle = strTitle.Substring(intLeft + 1, intRight - intLeft - 1);

            else if (strTitle.EndsWith(StrTtlEnd) &&
                     strTitle.Length > StrTtlEnd.Length)
            {
                strTitle = strTitle.Substring(0,
                    strTitle.Length - StrTtlEnd.Length);

                var intDot = strTitle.IndexOf(".", StringComparison.Ordinal);
                if (intDot > 0 && IsNumeric(strTitle.Substring(0, intDot)))
                    strTitle = strTitle.Remove(0, intDot + 1);
            }

            else
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

        private static void Command(int msg)
        {
            var hwnd = FindWindow(LpClassName, null);
            SendMessageW(hwnd, WmCommand, msg, 0);
        }

        public static void NextTrack()
        {
            Command(WaNexttrack);
        }

        public static void Stop()
        {
            Command(WaStop);
        }

        public static void Pause()
        {
            Command(WaPause);
        }

        public static void Play()
        {
            Command(WaPlay);
        }

        public static void PreviousTrack()
        {
            Command(WaPrevtrack);
        }
    }
}
