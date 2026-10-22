using VectorNNTPD.Common.Articles;
using VectorNNTPD.StorageService.Storage.SpoolPersistence;

namespace VectorNNTPD.StorageService.Tests.Storage
{

    public sealed class SpoolArticleIdentityGateTests
    {
        [Fact]
        public void Uncontended_acquire_removes_the_slot_and_a_second_dispose_is_a_no_op()
        {
            var gate = new SpoolArticleIdentityGate();
            var id = ArticleId.FromMessageId("<one@example.test>"u8);
            var lease = gate.Acquire(id, CancellationToken.None);
            Assert.Equal(1, gate.Reservations(id));
            lease.Dispose();
            lease.Dispose();
            Assert.Equal(0, gate.Reservations(id));
        }

        [Fact]
        public async Task The_same_id_waits_and_a_different_id_does_not()
        {
            var gate = new SpoolArticleIdentityGate();
            var id = ArticleId.FromMessageId("<same@example.test>"u8);
            var other = ArticleId.FromMessageId("<other@example.test>"u8);
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var first = gate.Acquire(id, CancellationToken.None);
            var waiting = Task.Run(() =>
            {
                using var second = gate.Acquire(id, CancellationToken.None);
                held.TrySetResult();
            });

            var started = Environment.TickCount64;
            while (gate.Reservations(id) < 2 && Environment.TickCount64 - started < 2000)
            {
                await Task.Yield();
            }

            Assert.Equal(2, gate.Reservations(id));
            using (gate.Acquire(other, CancellationToken.None))
            {
                Assert.Equal(1, gate.Reservations(other));
                Assert.False(held.Task.IsCompleted);
            }

            Assert.Equal(0, gate.Reservations(other));
            first.Dispose();
            await waiting.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, gate.Reservations(id));
        }

        [Fact]
        public async Task Cancelling_a_waiter_does_not_drop_the_holder()
        {
            var gate = new SpoolArticleIdentityGate();
            var id = ArticleId.FromMessageId("<cancel@example.test>"u8);
            using var holder = gate.Acquire(id, CancellationToken.None);
            using var cts = new CancellationTokenSource();
            var waiting = Task.Run(() => gate.Acquire(id, cts.Token));
            var started = Environment.TickCount64;
            while (gate.Reservations(id) < 2 && Environment.TickCount64 - started < 2000)
            {
                await Task.Yield();
            }

            Assert.Equal(2, gate.Reservations(id));
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
            Assert.Equal(1, gate.Reservations(id));
            holder.Dispose();
            Assert.Equal(0, gate.Reservations(id));
        }
    }
}
