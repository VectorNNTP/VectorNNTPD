using System.Text;
using Xunit;

namespace VectorNNTPD.ReaderService.Tests.Session.Framing;

public sealed class DataPlanePipelineTests
{
    [Fact]
    public async Task Pipeline_Synthetic_CheckQuitAndPayloadCommands()
    {
        var aStored = Encoding.ASCII.GetBytes("CHECK ignored\r\nQUIT ignored\r\nTAKETHIS ignored\r\n");
        var cStored = Encoding.ASCII.GetBytes("hello\r\n");
        using var ms = new MemoryStream();
        ms.Write(FramingWireFactory.BuildTakeThisTransaction("<a@test>", aStored));
        ms.Write("CHECK <b@test>\r\n"u8);
        ms.Write(FramingWireFactory.BuildTakeThisTransaction("<c@test>", cStored));
        ms.Write("QUIT\r\n"u8);
        ms.Write("TAKETHIS <after@quit>\r\nbody\r\n.\r\n"u8);

        var result = await ProductionTakethisStream.ParseAsync(ms.ToArray(), stopAfterQuit: true);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Articles);
        Assert.Equal(1, result.Checks);
        Assert.Equal(1, result.Quits);
        Assert.Equal("<a@test>", result.MessageIds[0]);
        Assert.Equal("<c@test>", result.MessageIds[1]);
        Assert.Equal(aStored, result.Payloads[0].ToArray());
        Assert.Equal(cStored, result.Payloads[1].ToArray());
    }

    [Fact]
    public async Task Pipeline_ControlPlane_CheckAndQuit()
    {
        var aStored = Encoding.ASCII.GetBytes("CHECK ignored\r\nQUIT ignored\r\nTAKETHIS ignored\r\n");
        var cStored = Encoding.ASCII.GetBytes("hello\r\n");
        using var ms = new MemoryStream();
        ms.Write(FramingWireFactory.BuildTakeThisTransaction("<a@test>", aStored));
        ms.Write(FramingWireFactory.BuildTakeThisTransaction("<c@test>", cStored));

        var result = await ProductionTakethisStream.ParseAsync(ms.ToArray());
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Articles);
        Assert.Equal("<a@test>", result.MessageIds[0]);
        Assert.Equal("<c@test>", result.MessageIds[1]);
    }

    [Fact]
    public async Task Pipeline_CommandsInsideArticle_ArePayload()
    {
        var stored = Encoding.ASCII.GetBytes("line\r\nCHECK x\r\nQUIT\r\nTAKETHIS y\r\n");
        var wire = FramingWireFactory.BuildTakeThisTransaction("<in@body>", stored);
        var result = await ProductionTakethisStream.ParseAsync(wire);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.Checks);
        Assert.Equal(0, result.Quits);
        Assert.Equal(1, result.Articles);
        Assert.Equal(stored, result.Payloads[0].ToArray());
    }

    [Fact]
    public async Task Pipeline_ResponseOrdering_Preserved()
    {
        using var ms = new MemoryStream();
        ms.Write(FramingWireFactory.BuildTakeThisTransaction("<1@t>", "a\r\n"u8));
        ms.Write(FramingWireFactory.BuildTakeThisTransaction("<2@t>", "b\r\n"u8));
        ms.Write(FramingWireFactory.BuildTakeThisTransaction("<3@t>", "c\r\n"u8));
        var result = await ProductionTakethisStream.ParseAsync(ms.ToArray());
        Assert.True(result.IsComplete);
        Assert.Equal(3, result.MessageIds.Count);
        Assert.Equal("<1@t>", result.MessageIds[0]);
        Assert.Equal("<2@t>", result.MessageIds[1]);
        Assert.Equal("<3@t>", result.MessageIds[2]);
    }

    [Fact]
    public async Task Pipeline_RoundTrip_StoredEqualsDestuffedCurrent()
    {
        var stored = Encoding.ASCII.GetBytes(".leading\r\n..two\r\nnormal\r\n");
        var wire = FramingWireFactory.BuildTakeThisTransaction("<rt@test>", stored);
        var result = await ProductionTakethisStream.ParseAsync(wire);
        Assert.True(result.IsComplete);
        Assert.Equal(stored.Length, result.ArticleBytes);
        Assert.Equal(stored, result.Payloads[0].ToArray());
    }
}
