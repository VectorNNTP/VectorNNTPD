using System.Globalization;
using System.Text;

namespace VectorNNTPD.TransitService.Networking.Transport
{
    /// <summary>DIAGNOSTIC-ONLY formatter for <see cref="TransportIoSnapshot"/>.</summary>
    internal static class TransportIoReport
    {
        /// <summary>Formats a human-readable cadence report.</summary>
        public static string Format(in TransportIoSnapshot snapshot, string sessionLabel)
        {
            var sb = new StringBuilder();
            sb.AppendLine("CONNECTION BYTE TRANSPORT I/O CADENCE — DIAGNOSTIC");
            sb.AppendLine();
            sb.AppendLine("Off-by-default. Production path is unchanged when VECTORNNTP_TRANSPORT_IO is unset.");
            sb.AppendLine("Clock: Stopwatch.GetTimestamp (monotonic). Durations in microseconds.");
            sb.AppendLine("Stream ops are NetworkStream/SslStream ReadAsync/WriteAsync/FlushAsync, not raw Socket APIs.");
            sb.AppendLine("Sync = ValueTask.IsCompletedSuccessfully before await. Await duration is 0 when sync.");
            sb.AppendLine("RX pipe flush is PipeWriter.FlushAsync after Advance (may include backpressure wait).");
            sb.AppendLine("TX pipe wait is PipeReader.ReadAsync and includes waiting for the application producer.");
            sb.AppendLine();
            sb.AppendLine($"Session:             {sessionLabel}");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Connection elapsed:  {snapshot.ConnectionElapsed.TotalMilliseconds:F3} ms"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"TX pipe pause:       {snapshot.TxPipePauseBytes:N0} bytes"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"TX pipe resume:      {snapshot.TxPipeResumeBytes:N0} bytes"));
            var experiment = TxPipePauseExperiment.IsConfigured ? "active (VECTORNNTP_TX_PIPE_PAUSE)" : "inactive (production 64 KiB)";
            sb.AppendLine($"TX pipe experiment:  {experiment}");
            var coalesce = snapshot.Coalesce.TargetBytes > 0
                ? $"active (VECTORNNTP_TX_WRITE_GRANULARITY {snapshot.Coalesce.TargetBytes:N0} bytes)"
                : "inactive (one WriteAsync per Pipe segment)";
            sb.AppendLine($"TX write coalesce:   {coalesce}");
            sb.AppendLine();

            WriteDirection(sb, "TX (stream WriteAsync)", snapshot.Tx, snapshot.TxWindow);
            WriteDirection(sb, "RX (stream ReadAsync)", snapshot.Rx, snapshot.RxWindow);

            sb.AppendLine("STREAM FlushAsync (not included in TX send stats)");
            WriteSimple(sb, snapshot.FlushOps, snapshot.FlushSync, snapshot.FlushAsync, snapshot.FlushUs);
            sb.AppendLine();

            sb.AppendLine("RX PipeWriter.FlushAsync (pipe processing / backpressure)");
            WriteSimple(sb, snapshot.RxPipeFlushes, snapshot.RxPipeSync, snapshot.RxPipeAsync, snapshot.RxPipeFlushUs);
            sb.AppendLine();

            sb.AppendLine("TX PipeReader.ReadAsync (includes producer wait; not socket time)");
            WriteSimple(sb, snapshot.TxPipeWaits, snapshot.TxPipeSync, snapshot.TxPipeAsync, snapshot.TxPipeWaitUs);
            sb.AppendLine();

            WriteReadShape(sb, snapshot.TxRead, snapshot.Tx.Ops);
            sb.AppendLine();

            WriteTxLoop(sb, snapshot);
            sb.AppendLine();
            WriteWriteCoalesce(sb, snapshot.Coalesce);
            sb.AppendLine();

            if (snapshot.Tx.Ops > 0 && snapshot.Tx.Bytes > 0)
            {
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"TX ops/MiB:          {snapshot.Tx.Ops / (snapshot.Tx.Bytes / (1024.0 * 1024.0)):F2}"));
            }

            if (snapshot.Rx.Ops > 0 && snapshot.Rx.Bytes > 0)
            {
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"RX ops/MiB:          {snapshot.Rx.Ops / (snapshot.Rx.Bytes / (1024.0 * 1024.0)):F2}"));
            }

            return sb.ToString();
        }

        /// <summary>Nearest-rank percentile of a sorted sample (microseconds).</summary>
        internal static double Percentile(int[] sorted, double p)
        {
            if (sorted.Length == 0)
            {
                return 0;
            }

            if (sorted.Length == 1)
            {
                return sorted[0];
            }

            var position = p * (sorted.Length - 1);
            var lo = (int)position;
            var hi = Math.Min(lo + 1, sorted.Length - 1);
            var frac = position - lo;
            return sorted[lo] + ((sorted[hi] - sorted[lo]) * frac);
        }

        internal static (double Mean, double P50, double P95, double P99, int Max) Stats(int[] samples)
        {
            if (samples.Length == 0)
            {
                return (0, 0, 0, 0, 0);
            }

            var copy = (int[])samples.Clone();
            Array.Sort(copy);
            long sum = 0;
            for (var i = 0; i < copy.Length; i++)
            {
                sum += copy[i];
            }

            return (sum / (double)copy.Length, Percentile(copy, 0.50), Percentile(copy, 0.95), Percentile(copy, 0.99), copy[^1]);
        }

        private static void WriteDirection(StringBuilder sb, string title, in TransportIoDirection d, TimeSpan window)
        {
            sb.AppendLine(title);
            sb.AppendLine($"  operations:        {d.Ops:N0}");
            sb.AppendLine($"  bytes:             {d.Bytes:N0}");
            sb.AppendLine($"  requested bytes:   {d.Requested:N0}");
            if (d.Ops > 0)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  avg bytes/op:      {d.Bytes / (double)d.Ops:F1}"));
                sb.AppendLine($"  min bytes/op:      {d.MinBytes:N0}");
                sb.AppendLine($"  max bytes/op:      {d.MaxBytes:N0}");
            }

            var gbit = window.TotalSeconds > 0 && d.Bytes > 0
                ? d.Bytes * 8d / window.TotalSeconds / 1_000_000_000d
                : 0;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  I/O window:        {window.TotalMilliseconds:F3} ms"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  throughput:        {gbit:F6} Gbit/s (bytes / I/O window)"));
            var total = d.Sync + d.Async;
            var syncPct = total > 0 ? 100.0 * d.Sync / total : 0;
            var asyncPct = total > 0 ? 100.0 * d.Async / total : 0;
            sb.AppendLine($"  sync complete:     {d.Sync:N0} ({syncPct:F1}%)");
            sb.AppendLine($"  async complete:    {d.Async:N0} ({asyncPct:F1}%)");
            WriteDuration(sb, "  op duration us", d.OpUs);
            WriteDuration(sb, "  await duration us", d.AwaitUs);
            sb.AppendLine();
        }

        private static void WriteSimple(StringBuilder sb, long ops, long sync, long async, int[] samples)
        {
            sb.AppendLine($"  operations:        {ops:N0}");
            var total = sync + async;
            var syncPct = total > 0 ? 100.0 * sync / total : 0;
            var asyncPct = total > 0 ? 100.0 * async / total : 0;
            sb.AppendLine($"  sync complete:     {sync:N0} ({syncPct:F1}%)");
            sb.AppendLine($"  async complete:    {async:N0} ({asyncPct:F1}%)");
            WriteDuration(sb, "  duration us", samples);
        }

        private static void WriteReadShape(StringBuilder sb, in TransportIoReadShape shape, long writeOps)
        {
            sb.AppendLine("TX Pipe ReadResult shape (non-empty; DIAGNOSTIC)");
            sb.AppendLine($"  reads:             {shape.Reads:N0}");
            sb.AppendLine($"  empty reads:       {shape.EmptyReads:N0}");
            sb.AppendLine($"  bytes:             {shape.Bytes:N0}");
            if (shape.Reads > 0)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  avg bytes/read:    {shape.Bytes / (double)shape.Reads:F1}"));
                sb.AppendLine($"  min bytes/read:    {shape.MinBytes:N0}");
                sb.AppendLine($"  max bytes/read:    {shape.MaxBytes:N0}");
            }

            sb.AppendLine($"  segments:          {shape.Segments:N0} (non-empty)");
            if (shape.Reads > 0)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  avg segments/read: {shape.Segments / (double)shape.Reads:F2}"));
                sb.AppendLine($"  min segments/read: {shape.MinSegments:N0}");
                sb.AppendLine($"  max segments/read: {shape.MaxSegments:N0}");
            }

            if (shape.Segments > 0)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  avg bytes/segment: {shape.Bytes / (double)shape.Segments:F1}"));
                sb.AppendLine($"  min bytes/segment: {shape.MinSegmentBytes:N0}");
                sb.AppendLine($"  max bytes/segment: {shape.MaxSegmentBytes:N0}");
            }

            if (shape.Reads > 0)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  writes/read:       {writeOps / (double)shape.Reads:F2} (stream WriteAsync / non-empty ReadResult)"));
            }

            sb.AppendLine($"  WriteAsync ops:    {writeOps:N0}");
            sb.AppendLine($"  segments vs writes: {shape.Segments:N0} segments -> {writeOps:N0} WriteAsync");
        }

        private static void WriteTxLoop(StringBuilder sb, in TransportIoSnapshot snapshot)
        {
            var loop = snapshot.Loop;
            sb.AppendLine("TX SendAsync loop stages (DIAGNOSTIC; NntpConnection.SendAsync)");
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  SendAsync elapsed: {loop.SendAsyncElapsed.TotalMilliseconds:F3} ms (pump lifetime, includes idle)"));
            sb.AppendLine($"  AdvanceTo calls:    {loop.AdvanceCount:N0}");
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  AdvanceTo total:    {loop.AdvanceTo.TotalMilliseconds:F3} ms"));
            sb.AppendLine($"  Transport writes:   {loop.TransportWriteCount:N0} (ConnectionByteTransport.WriteAsync)");
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  Transport write:    {loop.TransportWrite.TotalMilliseconds:F3} ms"));
            sb.AppendLine($"  Between-write gaps: {loop.BetweenWriteCount:N0}");
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  Between writes:     {loop.BetweenWrites.TotalMilliseconds:F3} ms"));
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  Read→first write:   {loop.ReadToFirstWrite.TotalMilliseconds:F3} ms"));
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  Last write→Advance: {loop.LastWriteToAdvance.TotalMilliseconds:F3} ms (includes FlushAsync)"));
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  Advance→next Read:  {loop.AdvanceToNextRead.TotalMilliseconds:F3} ms"));
            WriteDuration(sb, "  read→write us", loop.ReadToFirstWriteUs);
            WriteDuration(sb, "  between write us", loop.BetweenWriteUs);
            WriteDuration(sb, "  write→advance us", loop.LastWriteToAdvanceUs);
            WriteDuration(sb, "  AdvanceTo us", loop.AdvanceUs);
            WriteDuration(sb, "  advance→read us", loop.AdvanceToNextReadUs);
            WriteDuration(sb, "  transport wr us", loop.TransportWriteUs);

            var readWait = SumUs(snapshot.TxPipeWaitUs);
            var streamWrite = SumUs(snapshot.Tx.OpUs);
            var accounted = loop.ReadToFirstWrite + loop.BetweenWrites + loop.LastWriteToAdvance +
                            loop.AdvanceTo + loop.AdvanceToNextRead + loop.TransportWrite +
                            TimeSpan.FromMilliseconds(readWait / 1000.0);
            sb.AppendLine();
            sb.AppendLine("  Accounting (accumulated stage totals; ReadAsync wait uses sampled us sum)");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    ReadAsync waiting:     {readWait / 1000.0:F3} ms"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    Pipe → first Write:    {loop.ReadToFirstWrite.TotalMilliseconds:F3} ms"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    Transport WriteAsync:  {loop.TransportWrite.TotalMilliseconds:F3} ms"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    NetworkStream write:   {streamWrite / 1000.0:F3} ms (subset of transport write)"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    Between writes:        {loop.BetweenWrites.TotalMilliseconds:F3} ms"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    Last write → Advance:  {loop.LastWriteToAdvance.TotalMilliseconds:F3} ms"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    AdvanceTo:             {loop.AdvanceTo.TotalMilliseconds:F3} ms"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    Advance → next Read:   {loop.AdvanceToNextRead.TotalMilliseconds:F3} ms"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    Copy (in read→write):  {snapshot.Coalesce.CopyTime.TotalMilliseconds:F3} ms (subset of Pipe → first Write / between writes)"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    Accounted stages:      {accounted.TotalMilliseconds:F3} ms"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    SendAsync elapsed:     {loop.SendAsyncElapsed.TotalMilliseconds:F3} ms"));
            if (loop.SendAsyncElapsed > TimeSpan.Zero)
            {
                var unaccounted = loop.SendAsyncElapsed - accounted;
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"    Other/unaccounted:     {unaccounted.TotalMilliseconds:F3} ms (idle between commands is expected)"));
            }
        }

        private static void WriteWriteCoalesce(StringBuilder sb, in TransportIoWriteCoalesce coalesce)
        {
            sb.AppendLine("TX write granularity (DIAGNOSTIC; VECTORNNTP_TX_WRITE_GRANULARITY)");
            if (coalesce.TargetBytes <= 0)
            {
                sb.AppendLine("  target:             inactive");
                return;
            }

            sb.AppendLine($"  target:             {coalesce.TargetBytes:N0} bytes");
            sb.AppendLine($"  copy operations:    {coalesce.CopyCount:N0}");
            sb.AppendLine($"  copy bytes:         {coalesce.CopyBytes:N0}");
            if (coalesce.CopyCount > 0)
            {
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  avg copy bytes:     {coalesce.CopyBytes / (double)coalesce.CopyCount:F1}"));
            }

            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  copy time:          {coalesce.CopyTime.TotalMilliseconds:F3} ms"));
            WriteDuration(sb, "  copy us", coalesce.CopyUs);
        }

        private static long SumUs(int[] samples)
        {
            long sum = 0;
            for (var i = 0; i < samples.Length; i++)
            {
                sum += samples[i];
            }

            return sum;
        }

        private static void WriteDuration(StringBuilder sb, string label, int[] samples)
        {
            var s = Stats(samples);
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{label,-20} n={samples.Length,-7:N0} mean={s.Mean,10:F1} p50={s.P50,10:F1} p95={s.P95,10:F1} p99={s.P99,10:F1} max={s.Max,10:N0}"));
        }
    }
}
