using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

[ApiController]
[Route("api/[controller]")]
public class CfeController : ControllerBase
{
    private readonly CfeScraperService _scraperService;

    public CfeController(CfeScraperService scraperService)
    {
        _scraperService = scraperService;
    }

    [HttpGet("scrape")]
    public async Task<ActionResult<DashboardData>> Scrape()
    {
        var data = await _scraperService.ScrapeInconformidadesAsync();
        return Ok(data);
    }
}   