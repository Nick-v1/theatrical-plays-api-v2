﻿using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Theatrical.Dto.ResponseWrapperFolder;
using Theatrical.Services.Jwt;

namespace Theatrical.Api;

public class AdminAuthorizationFilter : IAuthorizationFilter
{
    private const string RequiredRole = "admin";
    private readonly ITokenService _tokenService;

    public AdminAuthorizationFilter(ITokenService tokenService)
    {
        _tokenService = tokenService;
    }
    
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        
        string? bearerToken = context.HttpContext.Request.Headers["Authorization"];
        
        if (string.IsNullOrEmpty(bearerToken))
        {
            // Handle unauthorized request
            var errorResponse = new ApiResponse(ErrorCode.Unauthorized,"You did not provide a JWT token");
            context.Result = new ObjectResult(errorResponse){StatusCode = (int)HttpStatusCode.Unauthorized};
            return;
        }

        if (!bearerToken.StartsWith("Bearer "))
        {
            //Handle incorrect bearer format
            var errorResponse1 = new ApiResponse(ErrorCode.BadRequest, "Correct Format: Bearer YourToken");
            context.Result = new ObjectResult(errorResponse1) { StatusCode = (int)HttpStatusCode.BadRequest };
            return;
        }

        string jwtToken = bearerToken.Substring(7);
        var claimsPrincipal = _tokenService.VerifyToken(jwtToken);
        
        if (claimsPrincipal is null)
        {
            var errorResponse2 = new ApiResponse(ErrorCode.InvalidToken, "Invalid Token");
            context.Result = new ObjectResult(errorResponse2) { StatusCode = (int)HttpStatusCode.Unauthorized };
            return;
        }

        if (!context.HttpContext.User.IsInRole(RequiredRole))
        {
            //Handle forbidden user
            var errorResponse3 = new ApiResponse(ErrorCode.Forbidden, "You are not allowed to make changes or see the context of this request");
            context.Result = new ObjectResult(errorResponse3){StatusCode = (int)HttpStatusCode.Forbidden};
            return;
        }
    }
}