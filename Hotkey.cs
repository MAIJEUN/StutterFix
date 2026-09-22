using UnityEngine;

namespace StutterFix
{
    // 단축키 (설정 창 열기, 모니터 바꾸기). 키 하나 + Ctrl/Shift/Alt 조합을 설정 창에서 바꿀 수 있다.
    // 조합은 정확히 맞아야 눌린 것으로 본다 (Insert 와 Shift+Insert 를 서로 다른 키로 쓰기 위해).
    internal static class Hotkey
    {
        internal const int Shift = 1, Ctrl = 2, Alt = 4;

        // 설정 창에서 새 키를 입력받는 중에는 모든 단축키를 멈춘다 (누른 키가 바로 창을 닫지 않게)
        internal static bool Capturing;

        internal static int Mods()
        {
            int m = 0;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) m |= Shift;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) m |= Ctrl;
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) m |= Alt;
            return m;
        }

        internal static bool Down(KeyCode key, int mods)
        {
            return !Capturing && key != KeyCode.None && Input.GetKeyDown(key) && Mods() == mods;
        }

        internal static bool IsModifier(KeyCode k)
        {
            return k == KeyCode.LeftShift || k == KeyCode.RightShift || k == KeyCode.LeftControl || k == KeyCode.RightControl
                || k == KeyCode.LeftAlt || k == KeyCode.RightAlt || k == KeyCode.LeftCommand || k == KeyCode.RightCommand
                || k == KeyCode.LeftWindows || k == KeyCode.RightWindows || k == KeyCode.AltGr;
        }

        // 얼불춤은 거의 모든 키를 박자 입력으로 쓴다. 조합 없이 이런 키를 고르면 플레이 중에 겹친다.
        internal static bool MayClashWithGame(KeyCode k, int mods)
        {
            if (mods != 0) return false;
            if (k >= KeyCode.F1 && k <= KeyCode.F15) return false;
            switch (k)
            {
                case KeyCode.Insert: case KeyCode.Home: case KeyCode.End: case KeyCode.PageUp: case KeyCode.PageDown:
                case KeyCode.Delete: case KeyCode.Pause: case KeyCode.ScrollLock: case KeyCode.Print:
                    return false;
            }
            return true;
        }

        internal static string Name(KeyCode k, int mods)
        {
            string s = "";
            if ((mods & Ctrl) != 0) s += "Ctrl + ";
            if ((mods & Shift) != 0) s += "Shift + ";
            if ((mods & Alt) != 0) s += "Alt + ";
            return s + KeyName(k);
        }

        private static string KeyName(KeyCode k)
        {
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return ((int)(k - KeyCode.Alpha0)).ToString();
            if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) return "Num " + (int)(k - KeyCode.Keypad0);
            switch (k)
            {
                case KeyCode.None: return "-";
                case KeyCode.PageUp: return "Page Up";
                case KeyCode.PageDown: return "Page Down";
                case KeyCode.BackQuote: return "`";
                case KeyCode.Minus: return "-";
                case KeyCode.Equals: return "=";
                case KeyCode.LeftBracket: return "[";
                case KeyCode.RightBracket: return "]";
                case KeyCode.Backslash: return "\\";
                case KeyCode.Semicolon: return ";";
                case KeyCode.Quote: return "'";
                case KeyCode.Comma: return ",";
                case KeyCode.Period: return ".";
                case KeyCode.Slash: return "/";
                case KeyCode.ScrollLock: return "Scroll Lock";
                default: return k.ToString();
            }
        }
    }
}
