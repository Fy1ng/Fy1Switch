using ManagedCommon;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RemoteDisplaySwitch.Probe.Tests;

[TestClass]
[DoNotParallelize]
public class LoggerTests
{
    [TestMethod]
    public void LogRedactsInstancePathAndHandle()
    {
        string? output = null;
        Logger.Sink = message => output = message;
        try
        {
            Logger.LogInfo(@"probe [DevicePath=\\?\DISPLAY#SAC2563#secret-instance#{guid}] handle=0x123ABC");
            Assert.IsNotNull(output);
            Assert.IsFalse(output.Contains("secret-instance", StringComparison.Ordinal));
            Assert.IsFalse(output.Contains("123ABC", StringComparison.Ordinal));
            StringAssert.Contains(output, "<display-id>");
        }
        finally
        {
            Logger.Sink = null;
        }
    }
}
