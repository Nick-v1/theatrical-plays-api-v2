﻿using Microsoft.AspNetCore.Mvc;
using Theatrical.Data.Models;
using Theatrical.Dto.LoginDtos;
using Theatrical.Dto.OrganizerDtos;
using Theatrical.Dto.ResponseWrapperFolder;
using Theatrical.Services;
using Theatrical.Services.Validation;

namespace Theatrical.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrganizersController : ControllerBase
{
    private readonly IOrganizerService _service;
    private readonly IOrganizerValidationService _validation;
    private readonly IUserValidationService _userValidation;
    
    public OrganizersController(IOrganizerService service, IOrganizerValidationService validation, IUserValidationService userService)
    {
        _service = service;
        _validation = validation;
        _userValidation = userService;
    }
    
    [HttpPost]
    public async Task<ActionResult<TheatricalResponse>> Create([FromBody] OrganizerCreateDto organizerCreateDto, [FromHeader] string? jwtToken)
    {
        var userValidation = _userValidation.ValidateUser(jwtToken);

        if (!userValidation.Success)
        {
            var responseError = new UserErrorMessage(userValidation.Message!).ConstructActionResult();
            return responseError;
        }
        
        await _service.Create(organizerCreateDto);

        var response = new TheatricalResponse("Successfully created Organizer");
        
        return new OkObjectResult(response);
    }

    [HttpGet]
    public async Task<ActionResult<TheatricalResponse>> GetOrganizers()
    {
        var (report, organizers) = await _validation.ValidateAndFetch();

        if (!report.Success)
        {
            var errorResponse = new TheatricalResponse(ErrorCode.NotFound, report.Message);
            return new NotFoundObjectResult(errorResponse);
        }
        
        var response = new TheatricalResponse<List<Organizer>>(organizers);
        
        return new ObjectResult(response);
    }

    
    [HttpDelete]
    [Route("{id}")]
    public async Task<ActionResult<TheatricalResponse>> DeleteOrganizer(int id, [FromHeader] string? jwtToken)
    {
        var userValidation = _userValidation.ValidateUser(jwtToken);
        
        if (!userValidation.Success)
        {
            var responseError = new UserErrorMessage(userValidation.Message!).ConstructActionResult();
            return responseError;
        }
        
        var (report, organizer) = await _validation.ValidateForDelete(id);

        if (!report.Success)
        {
            var errorResponse = new TheatricalResponse(ErrorCode.NotFound, report.Message);
            return new NotFoundObjectResult(errorResponse);
        }

        await _service.Delete(organizer);
        var response = new TheatricalResponse<Organizer>(organizer, message: "Organizer has been deleted");
        return new OkObjectResult(response);
    }
}