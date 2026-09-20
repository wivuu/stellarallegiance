// Console suite for the game-server auto-update feature (server/Update + shared/ReleaseVersion). One
// group per file; each prints its own `ok`/`FAIL` lines. Exit code 0 = green, like every suite under tests/.
ReleaseVersionTests.Run();
OptionsTests.Run();
CoordinatorTests.Run();
FuzzTests.Run();
StatePathTests.Run();
RelauncherTests.Run();
HubGateTests.Run();

Console.WriteLine();
Console.WriteLine(T.Failures == 0 ? $"ALL {T.Passed} CHECKS PASSED" : $"{T.Failures} FAILED, {T.Passed} passed");
return T.Failures == 0 ? 0 : 1;
