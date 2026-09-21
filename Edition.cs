namespace StutterFix
{
    // 한 소스로 두 가지 DLL 을 만든다.
    //   개발자용 (기본, DEV 정의): 끊김 기록, 단계/그리기/함수 측정, Ctrl+F5 다시 불러오기, F6~F9 진단
    //   플레이어용 (dotnet build -p:Edition=Player): 효과가 측정된 수정만. 측정 패치를 걸지 않고 로그도 거의 안 남긴다.
    // 진단 코드는 두 쪽 다 컴파일되지만, 플레이어용에서는 설치·호출되지 않는다.
    internal static class Edition
    {
#if DEV
        internal const bool Dev = true;
        internal const string Name = "개발자용";
#else
        internal const bool Dev = false;
        internal const string Name = "플레이어용";
#endif
    }
}
