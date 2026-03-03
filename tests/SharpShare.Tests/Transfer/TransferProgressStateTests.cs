using SharpShare.Transfer;

namespace SharpShare.Tests.Transfer;

public class TransferProgressStateTests
{
    [Test]
    public async Task ProgressFraction_ReturnsCorrectPercentage()
    {
        var state = new TransferProgressState
        {
            TransferId = 1,
            FileName = "test.bin",
            TotalBytes = 1000,
            Direction = TransferDirection.Download,
        };

        Interlocked.Exchange(ref state.BytesTransferred, 500);

        await Assert.That(state.ProgressFraction).IsEqualTo(0.5);
    }

    [Test]
    public async Task ProgressFraction_ZeroTotalBytes_ReturnsZero()
    {
        var state = new TransferProgressState
        {
            TransferId = 1,
            FileName = "empty.bin",
            TotalBytes = 0,
            Direction = TransferDirection.Download,
        };

        await Assert.That(state.ProgressFraction).IsEqualTo(0.0);
    }

    [Test]
    public async Task ProgressFraction_FullyTransferred_ReturnsOne()
    {
        var state = new TransferProgressState
        {
            TransferId = 1,
            FileName = "done.bin",
            TotalBytes = 2048,
            Direction = TransferDirection.Upload,
        };

        Interlocked.Exchange(ref state.BytesTransferred, 2048);

        await Assert.That(state.ProgressFraction).IsEqualTo(1.0);
    }
}
