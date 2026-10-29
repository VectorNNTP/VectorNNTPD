using System.Text;
using VectorNNTPD.TransitService.Email;

namespace VectorNNTPD.TransitService.Tests.Email;

public sealed class Rfc5322MessageEncoderTests
{
    private readonly Rfc5322MessageEncoder _encoder = new();

    [Fact]
    public void Encode_UsesCrlfAndOmitsBcc()
    {
        var message = Create(
            subject: "Hello",
            body: "line1\nline2",
            bcc: new EmailAddress("hidden@example.com"));
        var text = Encoding.ASCII.GetString(_encoder.Encode(message).Span);
        Assert.Contains("From: from@example.com\r\n", text, StringComparison.Ordinal);
        Assert.Contains("To: to@example.com\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Subject: Hello\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Cc: cc@example.com\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bcc:", text, StringComparison.Ordinal);
        Assert.Contains("line1\r\nline2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\nline1", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_EncodesNonAsciiSubject()
    {
        var message = Create(subject: "café");
        var text = Encoding.ASCII.GetString(_encoder.Encode(message).Span);
        Assert.Contains("=?UTF-8?B?", text, StringComparison.Ordinal);
        Assert.DoesNotContain("café", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_RejectsHeaderInjection()
    {
        Assert.Throws<ArgumentException>(() =>
            _encoder.Encode(Create(subject: "ok\r\nBcc: evil@example.com")));
        Assert.Throws<ArgumentException>(() =>
            new EmailHeader("X-Injected\r\nBcc", "x").Validate());
        Assert.Throws<ArgumentException>(() =>
            new EmailHeader("X-Good", "bad\r\nBcc: evil@example.com").Validate());
        Assert.Throws<ArgumentException>(() =>
            new EmailAddress("ok@example.com", "bad\r\nFrom: evil"));
    }

    [Fact]
    public void Encode_CustomHeadersAndNewsTransmission()
    {
        var message = new EmailMessage
        {
            From = new EmailAddress("from@example.com"),
            To = [new EmailAddress("to@example.com")],
            Subject = "group mid",
            Body = "From: poster@example.com\r\n\r\nbody\r\n"u8.ToArray(),
            ContentType = "application/news-transmission; usage=moderate",
            Headers = [new EmailHeader("X-Moderated-Newsgroup", "comp.example")],
        };
        var text = Encoding.ASCII.GetString(_encoder.Encode(message).Span);
        Assert.Contains("Content-Type: application/news-transmission; usage=moderate\r\n", text, StringComparison.Ordinal);
        Assert.Contains("X-Moderated-Newsgroup: comp.example\r\n", text, StringComparison.Ordinal);
        Assert.Contains("usage=moderate", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_QuotedPrintableFor8BitBody()
    {
        var message = Create(bodyBytes: "naïve"u8.ToArray());
        var text = Encoding.ASCII.GetString(_encoder.Encode(message).Span);
        Assert.Contains("Content-Transfer-Encoding: quoted-printable", text, StringComparison.Ordinal);
        Assert.Contains("=C3=AF", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_AttachmentIsMultipartBase64()
    {
        var message = Create();
        message = new EmailMessage
        {
            From = message.From,
            To = message.To,
            Subject = message.Subject,
            Body = message.Body,
            Attachments = [new EmailAttachment("note.txt", "hi"u8.ToArray(), "text/plain")],
        };
        var text = Encoding.ASCII.GetString(_encoder.Encode(message).Span);
        Assert.Contains("multipart/mixed", text, StringComparison.Ordinal);
        Assert.Contains("filename=\"note.txt\"", text, StringComparison.Ordinal);
        Assert.Contains("aGk=", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvelopeRecipients_AreDistinct()
    {
        var message = Create(bcc: new EmailAddress("to@example.com"));
        Assert.Equal(2, message.EnvelopeRecipients().Count);
    }

    private static EmailMessage Create(
        string subject = "Subject",
        string body = "body\r\n",
        byte[]? bodyBytes = null,
        EmailAddress? bcc = null) =>
        new()
        {
            From = new EmailAddress("from@example.com"),
            To = [new EmailAddress("to@example.com")],
            Cc = [new EmailAddress("cc@example.com")],
            Bcc = bcc is null ? [] : [bcc],
            Subject = subject,
            Body = bodyBytes ?? Encoding.ASCII.GetBytes(body),
        };
}
