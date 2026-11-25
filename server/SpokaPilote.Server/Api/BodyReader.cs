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
    public sealed record Result(string? Text, IResult? Error);

    public static async Task<Result> ReadAsync(HttpRequest request, long limit, string what, CancellationToken ct)
    {
        var tooLarge = Results.Json(new ApiError($"{what} trop volumineux (limite : {limit / 1024 / 1024.0:0.#} Mo)."),
            statusCode: StatusCodes.Status413PayloadTooLarge);
        if (request.ContentLength > limit) return new Result(null, tooLarge);

        var encoding = request.Headers.ContentEncoding.ToString().Trim().ToLowerInvariant();
        if (encoding is not ("" or "identity" or "gzip"))
            return new Result(null, Results.Json(new ApiError($"Encodage « {encoding} » non pris en charge (gzip ou aucun)."),
                statusCode: StatusCodes.Status415UnsupportedMediaType));

        var gzip = encoding == "gzip" ? new GZipStream(request.Body, CompressionMode.Decompress, leaveOpen: true) : null;
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
            return new Result(new UTF8Encoding(false).GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('﻿'), null);
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
