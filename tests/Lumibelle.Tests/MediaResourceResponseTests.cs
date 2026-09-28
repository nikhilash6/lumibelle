using System.Text;
using lumibelle.Models;
using lumibelle.Services;

namespace Lumibelle.Tests;
public sealed class MediaResourceResponseTests
{
    private static readonly DateTimeOffset Modified = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static AssetMedia Media(Stream? stream = null) => new(stream ?? new MemoryStream(Encoding.ASCII.GetBytes("0123456789")), "video/mp4", Modified);

    [Theory]
    [InlineData("bytes=2-4", 206, "234", "bytes 2-4/10")]
    [InlineData("bytes=8-", 206, "89", "bytes 8-9/10")]
    [InlineData("bytes=-3", 206, "789", "bytes 7-9/10")]
    [InlineData("bytes=8-999", 206, "89", "bytes 8-9/10")]
    [InlineData("bytes=20-", 416, "", "bytes */10")]
    [InlineData("bytes=4-2", 416, "", "bytes */10")]
    [InlineData("bytes=-0", 416, "", "bytes */10")]
    public async Task RangesReturnExactStatusHeadersAndBytes(string range, int status, string body, string contentRange)
    {
        await using var response = MediaResources.Respond(Media(), "GET", new Dictionary<string,string> { ["range"] = range });
        Assert.Equal(status, response.Status); Assert.Equal(contentRange, response.Headers["Content-Range"]);
        Assert.Equal(body.Length.ToString(), response.Headers["Content-Length"]);
        Assert.Equal(body, await new StreamReader(response.Content).ReadToEndAsync(TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task SliceIsSeekableWithoutEscapingItsRangeAndOwnsTheSource()
    {
        var source = new MemoryStream(Encoding.ASCII.GetBytes("0123456789"));
        await using (var response = MediaResources.Respond(Media(source), "GET", new Dictionary<string,string> { ["Range"] = "bytes=2-4" }))
        {
            Assert.True(response.Content.CanSeek); Assert.Equal(3, response.Content.Length);
            Assert.Equal('2', response.Content.ReadByte()); response.Content.Seek(-1, SeekOrigin.End); Assert.Equal('4', response.Content.ReadByte());
            Assert.Equal(-1, response.Content.ReadByte()); Assert.Throws<IOException>(() => response.Content.Seek(4, SeekOrigin.Begin));
        }
        Assert.False(source.CanRead);
    }
    [Fact]
    public async Task HeadAndConditionalResponsesCloseTheFileAndCarryNoBody()
    {
        var source = new MemoryStream(new byte[10]);
        await using var head = MediaResources.Respond(Media(source), "HEAD");
        Assert.Equal("10", head.Headers["Content-Length"]); Assert.Same(Stream.Null, head.Content); Assert.False(source.CanRead);
        var conditional = MediaResources.Respond(Media(), "GET", new Dictionary<string,string> { ["If-Modified-Since"] = Modified.ToString("R") });
        Assert.Equal(304, conditional.Status); Assert.Same(Stream.Null, conditional.Content);
        await using var staleRange = MediaResources.Respond(Media(), "GET", new Dictionary<string,string> { ["Range"] = "bytes=2-4", ["If-Range"] = Modified.AddDays(-1).ToString("R") });
        Assert.Equal(200, staleRange.Status); Assert.Equal("10", staleRange.Headers["Content-Length"]);
    }
    [Fact]
    public async Task LargeResourceIsNotReadDuringResolutionAndCancellationReachesItsStream()
    {
        using var stream = new SparseStream(8L * 1024 * 1024 * 1024);
        await using var response = MediaResources.Respond(Media(stream), "GET", new Dictionary<string,string> { ["Range"] = "bytes=4294967296-" });
        Assert.Equal(0, stream.Reads); Assert.Equal("4294967296", response.Headers["Content-Length"]);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { _ = await response.Content.ReadAsync(new byte[1024], cancelled.Token); });
        Assert.Equal(0, stream.Reads);
        Assert.Equal(1024, await response.Content.ReadAsync(new byte[1024], TestContext.Current.CancellationToken)); Assert.Equal(1, stream.Reads);
    }
    private sealed class SparseStream(long length) : Stream
    {
        public int Reads; public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => true;
        public override long Length => length; public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) { Reads++; var n = (int)Math.Min(count, length - Position); Position += n; return n; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); Reads++; var n = (int)Math.Min(buffer.Length, length - Position); Position += n; return ValueTask.FromResult(n); }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => length + offset };
        public override void Flush() { } public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
