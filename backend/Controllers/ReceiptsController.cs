using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/receipts")]
public class ReceiptsController(IReceiptExtractor extractor) : ControllerBase
{
    [HttpPost("extract")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Extract(IFormFile image)
    {
        if (image is null || image.Length == 0)
            return BadRequest(new { message = "No image provided." });

        var mediaType = image.ContentType switch
        {
            "image/png"  => "image/png",
            "image/webp" => "image/webp",
            "image/gif"  => "image/gif",
            _            => "image/jpeg",
        };

        using var ms = new MemoryStream();
        await image.CopyToAsync(ms);

        try
        {
            var result = await extractor.ExtractAsync(ms.ToArray(), mediaType);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { message = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { message = $"AI service error: {ex.Message}" });
        }
    }
}
