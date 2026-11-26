using System.IO.Compression;
using System.Text;
using SpokaPilote.Server.Security;

namespace SpokaPilote.Server.Api;

/// <summary>
/// Lecture bornée du corps d'une requête, compressé (Content-Encoding: gzip) ou non. La limite s'applique à la taille
/// décompressée : une « bombe » gzip est interrompue dès que la limite est franchie.
/// </summary>
public static class BodyReader
{
    /// <param name="Compressed">Corps gzip tel que reçu (si demandé) : conservé sans recompresser.</param>
    public sealed record Result(string? Text, IResult? Error, byte[]? Compressed = null);

    public static async Task<Result> ReadAsync(HttpRequest request, long limit, string what, CancellationToken ct, bool keepCompressed = false)
    {
        var tooLarge = Results.Json(new ApiError($"{what} trop volumineux (limite : {limit / 1024 / 1024.0:0.#} Mo)."),
            statusCode: StatusCodes.Status413PayloadTooLarge);
        if (request.ContentLength > limit) return new Result(null, tooLarge);

        var encoding = request.Headers.ContentEncoding.ToString().Trim().ToLowerInvariant();
        if (encoding is not ("" or "identity" or "gzip"))
            return new Result(null, Results.Json(new ApiError($"Encodage « {encoding} » non pris en charge (gzip ou aucun)."),
                statusCode: StatusCodes.Status415UnsupportedMediaType));

        var capture = encoding == "gzip" && keepCompressed ? new CaptureStream(request.Body) : null;
        var gzip = encoding == "gzip" ? new GZipStream((Stream?)capture ?? request.Body, CompressionMode.Decompress, leaveOpen: true) : null;
        var source = gzip ?? request.Body;
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit) return new Result(null, tooLarge);
                buffer.Write(chunk, 0, read);
            }
            if (buffer.Length == 0)
                return new Result(null, Results.Json(new ApiError($"{what} vide."), statusCode: StatusCodes.Status400BadRequest));
            // UTF-8 avec ou sans BOM (Windows PowerShell 5.1 en ajoute un).
            return new Result(new UTF8Encoding(false).GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('﻿'), null, capture?.ToArray());
        }
        catch (InvalidDataException)
        {
            return new Result(null, Results.Json(new ApiError("Contenu gzip invalide."), statusCode: StatusCodes.Status400BadRequest));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return new Result(null, tooLarge);
        }
        finally
        {
            if (gzip is not null) await gzip.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Flux en lecture seule qui garde une copie de ce qui est lu (corps gzip conservé tel que reçu).</summary>
internal sealed class CaptureStream(Stream inner) : Stream
{
    private readonly MemoryStream _copy = new();

    public byte[] ToArray() => _copy.ToArray();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        _copy.Write(buffer, offset, n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _copy.Write(buffer.Span[..n]);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
