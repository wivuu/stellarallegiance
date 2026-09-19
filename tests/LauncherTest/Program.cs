// Console suite for launcher/Core — the Game Launcher's UI-free heart. One group per file; each prints
// its own `ok`/`FAIL` lines. Exit code = number of failures (0 = green), like every suite under tests/.
ArgsTests.Run();
GameTests.Run();
SettingsTests.Run();
FlowTests.Run();
ThemeTests.Run();
LobbyTests.Run();
NotesTests.Run();
InfraTests.Run();

Console.WriteLine();
Console.WriteLine(T.Failures == 0 ? $"ALL {T.Passed} CHECKS PASSED" : $"{T.Failures} FAILED, {T.Passed} passed");
return T.Failures == 0 ? 0 : 1;
