﻿using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Theatrical.Data.enums;
using Theatrical.Dto.ResponseWrapperFolder;
using Theatrical.Dto.UsersDtos;
using Theatrical.Dto.UsersDtos.ResponseDto;
using Theatrical.Services;
using Theatrical.Services.Email;
using Theatrical.Services.PhoneVerification.Twilio;
using Theatrical.Services.Security.AuthorizationFilters;
using Theatrical.Services.Validation;

namespace Theatrical.Api.Controllers;

/// <summary>
/// This controller contains the basic operations for user related functions.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[EnableCors("AllowOrigin")]
public class UserController : ControllerBase
{
    private readonly IUserValidationService _validation;
    private readonly IUserService _service;
    private readonly IEmailService _emailService;
    private readonly IMinioService _minioService;
    private readonly ITransactionService _transactions;
    private readonly ITwilioService _twilio;

    public UserController(IUserValidationService validation, IUserService service, IEmailService emailService, IMinioService minioService,
        ITransactionService transactionService, ITwilioService twilioService)
    {
        _validation = validation;
        _service = service;
        _emailService = emailService;
        _minioService = minioService;
        _transactions = transactionService;
        _twilio = twilioService;
    }
    
    /// <summary>
    /// Use this method to register.
    /// Use 1 for admin account or
    /// Use 2 for user account or
    /// Use 3 for developer account.
    /// If you don't define role, user account will be created.
    /// </summary>
    /// <param name="registerUserDto"></param>
    /// <returns></returns>
    [HttpPost("register")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(RegisterUserResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse>> Register([FromBody] RegisterUserDto registerUserDto)
    {
        try
        {
            var validation = await _validation.ValidateForRegister(registerUserDto);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new BadRequestObjectResult(errorResponse);
            }

            //Generate the verification token
            var verificationToken = Guid.NewGuid().ToString();

            //URI to verification endpoint.
            var verificationUrl = $"{Request.Scheme}://{Request.Host}/api/user/verify-email?token={verificationToken}";

            //Send confirmation email to the registered user.
            await _emailService.SendConfirmationEmailAsync(registerUserDto.Email, verificationUrl);
            
            var userCreated = await _service.Register(registerUserDto, verificationToken);
            var response = new ApiResponse<RegisterUserResponseDto>(userCreated, "Successfully Registered!");
            
            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError}; 
        }
    }
    
    /// <summary>
    /// Use this to login in.
    /// Checks the request authorization header. (If the user is already logged in).
    /// Validates the user.
    /// Two factor authentication logic, for users who have enabled 2fa.
    /// After these checks, it provides the user with a JWT.
    /// Use the token for locked actions.
    /// </summary>
    /// <param name="loginUserDto"></param>
    /// <returns></returns>
    [HttpPost("login")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(JwtDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse>> Login([FromBody]LoginUserDto loginUserDto)
    {
        try
        {
            var authorizationHeader = Request.Headers["Authorization"].FirstOrDefault();
            
            var authHeaderReport = _validation.ValidateAuthorizationHeader(authorizationHeader);

            if (!authHeaderReport.Success)
            {
                var loggedInResponse =
                    new ApiResponse((ErrorCode)authHeaderReport.ErrorCode!, authHeaderReport.Message!);
                return new ConflictObjectResult(loggedInResponse);
            }
            
            var (validationReport, user) = await _validation.ValidateForLogin(loginUserDto);

            if (!validationReport.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validationReport.ErrorCode!, validationReport.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }


            //2fa authentication logic
            if (validationReport.ErrorCode.Equals(ErrorCode._2FaEnabled))
            {
                var errorResponse2Fa = new ApiResponse((ErrorCode)validationReport.ErrorCode, validationReport.Message!);

                //Creates the 2fa code
                var totpCode = _service.GenerateOTP(user!);
                    
                //Sends an email to the user with the 2fa code
                await _emailService.Send2FaVerificationCode(user!, totpCode);
                    
                //Saves the code.
                await _service.Save2FaCode(user!, totpCode);
                        
                return new ObjectResult(errorResponse2Fa){StatusCode = (int) HttpStatusCode.Conflict};
            }

            var jwtDto = _service.GenerateToken(user!);

            var response = new ApiResponse<JwtDto>(jwtDto);

            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    [HttpPost]
    [Route("register/phoneNumber")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    public async Task<IActionResult> RegisterMobilePhone([FromQuery] string phoneNumber)
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
            
            var (validation, user) = await _validation.ValidateUser(email!);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var phoneValidated = _validation.ValidateNumber(user!);

            if (!phoneValidated.Success)
            {
                return new BadRequestObjectResult(new ApiResponse(ErrorCode.BadRequest, phoneValidated.Message!));
            }

            await _service.RegisterPhoneNumber(user!, phoneNumber);
            
            return Ok(new ApiResponse("Successfully Registered your Number!"));
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError}; 
        }
    }

    [HttpDelete]
    [Route("remove/phoneNumber")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    public async Task<IActionResult> RemovePhoneNumber()
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
            
            var (validation, user) = await _validation.ValidateUser(email!);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            if (user!.PhoneNumber is null)
            {
                return new ObjectResult(new ApiResponse("You don't have a registered number!"));
            }

            await _service.RemovePhoneNumber(user);
            var response = new ApiResponse("Successfully removed your number!");
            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError}; 
        }
    }
    
    [HttpPost("request-verification-phone-number")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    public async Task<IActionResult> SendVerificationCode()
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
            
            var (validation, user) = await _validation.ValidateUser(email!);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            if (user!.PhoneNumber is null)
            {
                var notRegisteredResponse = new ApiResponse(ErrorCode.BadRequest,"You don't have a registered number to verify!");
                return new BadRequestObjectResult(notRegisteredResponse);
            }

            if (user!.PhoneNumberVerified == true)
            {
                var verifiedResponse = new ApiResponse("Your phone is already verified!");
                return new OkObjectResult(verifiedResponse);
            }
            
            var userBalance = user.UserTransactions.Sum(t => t.CreditAmount);

            const decimal cost = 0.20M;
            if (userBalance - cost < 0)
            {
                var insufficientBalanceError = new ApiResponse(ErrorCode.InsufficientBalance, "Not enough balance to verify. Cost: 0.20€");
                return new BadRequestObjectResult(insufficientBalanceError);
            }
            
            //Use twilio to send verification code to user's phone number
            var result = await _twilio.SendVerificationCode(user.PhoneNumber);

            if (!result.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)result.ErrorCode!, result.Message!);
                return new BadRequestObjectResult(errorResponse);
            }
            
            //charge user.
            await _transactions.VerificationPhoneNumberCost(user, cost);

            var response = new ApiResponse($"Sent your verification to your number. State: {result.Message!}");

            return new OkObjectResult(response);
        }
        catch (Exception)
        {
            return BadRequest("There was an error sending the verification code, please check the phone number is correct and try again");
        }
    }
    
    [HttpPost("confirm-verification-phone-number")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    public async Task<IActionResult> CheckVerificationCode(string verificationCode)
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
            
            var (validation, user) = await _validation.ValidateUser(email!);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            if (user!.PhoneNumber is null)
            {
                return new BadRequestObjectResult(new ApiResponse(ErrorCode.BadRequest,"You don't have a registered number to confirm!"));
            }
            
            var result = await _twilio.CheckVerificationCode(user!.PhoneNumber!, verificationCode);
            
            if (!result.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)result.ErrorCode!, result.Message!);
                return new BadRequestObjectResult(errorResponse);
            }

            await _service.UpdateVerifiedPhoneNumber(user, user.PhoneNumber!);
            
            var response = new ApiResponse($"Your number has been {result.Message!}!");

            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError}; 
        }
    }

    /// <summary>
    /// Verification link points to this endpoint.
    /// Verifies the user and enabled the account.
    /// Redirects to a page with the response. (For user friendly experience)
    /// </summary>
    /// <param name="token">verification code</param>
    /// <returns></returns>
    [HttpGet("verify-email")]
    public async Task<IActionResult> VerifyEmail([FromQuery]string token)
    {
        try
        {
            var (verification, user) = await _validation.VerifyEmailToken(token);
            
            if (!verification.Success)
            {
                if (verification.ErrorCode == ErrorCode.AlreadyVerified)
                {
                    return Redirect($"/EmailVerification?status=already-verified");
                }
                return Redirect($"/EmailVerification?status=failed");
            }

            await _service.EnableAccount(user!);
            await _transactions.VerifiedEmailCredits(user!);
            
            return Redirect($"/EmailVerification?status=success");
        }
        catch (Exception e)
        {
            return Redirect($"/EmailVerification?status=internal-error");
        }
    }

    /// <summary>
    /// Enables two factor authentication for the logged in user.
    /// Checks authorization header and retrieves the email => User validation logic.
    /// If successful 2fa is enabled!
    /// If not it provides user with the appropriate message.
    /// Any role can use this endpoint.
    /// </summary>
    /// <returns></returns>
    [HttpPost("enable2fa")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> EnableTwoFactorAuth()
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateFor2FaActivation(email!);

            if (!validation.Success)
            {
                if (validation.ErrorCode.Equals(ErrorCode.NotFound))
                {
                    var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                    return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
                }
                
                if (validation.ErrorCode.Equals(ErrorCode.InvalidEmail))
                {
                    var errorEmailResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                    return new BadRequestObjectResult(errorEmailResponse);
                }

                var errorResponseConflict = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponseConflict) { StatusCode = (int)HttpStatusCode.Conflict };
            }
            
            await _service.ActivateTwoFactorAuthentication(user!);

            await _emailService.SendConfirmationEmailTwoFactorActivated(user!.Email);

            var response = new ApiResponse("Two Factor Authentication Activated!");

            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    /// <summary>
    /// Disables two factor authentication for the logged in user.
    /// Checks authorization header and retrieves the email => User validation logic.
    /// If successful 2fa is disabled!
    /// If not it provides user with the appropriate message.
    /// Any role can use this endpoint.
    /// </summary>
    /// <returns></returns>
    [HttpPost("disable2fa")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse>> DisableTwoFactorAuth()
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
            
            var (validation, user) = await _validation.ValidateFor2FaDeactivation(email!);

            //Executes when something fails. Or if two factor authentication is already disabled.
            if (!validation.Success)
            {
                if (validation.ErrorCode.Equals(ErrorCode.NotFound))
                {
                    var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                    return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
                }

                if (validation.ErrorCode.Equals(ErrorCode._2FaDisabled))
                {
                    var errorResponseAlreadyDisabled = new ApiResponse((ErrorCode)validation.ErrorCode, validation.Message!);
                    return new ObjectResult(errorResponseAlreadyDisabled) { StatusCode = (int)HttpStatusCode.Conflict };
                }
            }

            //Success Scenario.---------------------------------------------------------------------------------------------
            await _service.DeactivateTwoFactorAuthentication(user!);

            await _emailService.SendConfirmationEmailTwoFactorDeactivated(user!.Email);

            var response = new ApiResponse("Two Factor Authentication Disabled.");

            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    /// <summary>
    /// Use this after getting your one time passcode from email.
    /// Verifies the code,
    /// Generates a login token (jwt),
    /// Sends appropriate reply.
    /// </summary>
    /// <param name="code"></param>
    /// <returns></returns>
    [HttpPost("login/2fa/{code}")]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse>> Login2Fa([FromRoute]int code)
    {
        try
        {
            var (validation, user) = await _validation.VerifyOtp(code.ToString().Trim());

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.BadRequest };
            }

            var jwtDto = _service.GenerateToken(user!);

            var response = new ApiResponse<JwtDto>(jwtDto, validation.Message!);

            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    /// <summary>
    /// Provides the balance of a user.
    /// Only admins can use this request.
    /// </summary>
    /// <param name="id">user's id</param>
    /// <returns>Available User Credits</returns>
    [HttpGet("{id}/balance")]
    [ServiceFilter(typeof(AdminAuthorizationFilter))]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> Balance([FromRoute]int id)
    {
        try
        {
            var (validationReport, credits) = await _validation.ValidateBalance(id);

            if (!validationReport.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validationReport.ErrorCode!, validationReport.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var response = new ApiResponse<string>($"You have {credits} credits.");

            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    [HttpGet("info")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse>> GetConnectedUserInfo()
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email!);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }
            
            var userDto = await _service.ConstructUserInfo(user!);

            var response = new ApiResponse<UserDto>(userDto);

            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    [HttpGet("refresh-token")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    [ProducesResponseType(typeof(JwtDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse>> RefreshToken()
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
            var (validation, user) = await _validation.ValidateUser(email!);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var jwtDto = _service.GenerateToken(user!);

            var response = new ApiResponse<JwtDto>(jwtDto, "Token refreshed!");

            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    [HttpDelete]
    [Route("@/facebook")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> DeleteFacebook()
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email!);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var mediaValidation = _validation.ValidateSocialMediaForDelete(user!, SocialMedia.Facebook);
            if (!mediaValidation.Success)
            {
                var mediaErrorResponse = new ApiResponse((ErrorCode)mediaValidation.ErrorCode!, mediaValidation.Message!);
                return new ObjectResult(mediaErrorResponse){StatusCode = 404};
            }

            await _service.RemoveSocialMedia(user!, SocialMedia.Facebook);

            var apiResponse = new ApiResponse($"Successfully removed your {SocialMedia.Facebook} account.");
            
            return new OkObjectResult(apiResponse);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }
    
    [HttpPut]
    [Route("@/facebook")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse>> UpdateFacebook([FromQuery] string link)
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email!);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var linkValidation = _validation.ValidateFacebookLink(link);

            if (!linkValidation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)linkValidation.ErrorCode!, linkValidation.Message!);
                return new ObjectResult(errorResponse){StatusCode = (int)HttpStatusCode.BadRequest};
            }

            await _service.UpdateFacebook(user!, link);

            var response = new ApiResponse("Successfully updated facebook link.");

            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
        
    }
    
    [HttpDelete]
    [Route("@/youtube")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> DeleteYoutube()
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email!);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var mediaValidation = _validation.ValidateSocialMediaForDelete(user!, SocialMedia.Youtube);
            if (!mediaValidation.Success)
            {
                var mediaErrorResponse = new ApiResponse((ErrorCode)mediaValidation.ErrorCode!, mediaValidation.Message!);
                return new ObjectResult(mediaErrorResponse){StatusCode = 404};
            }

            await _service.RemoveSocialMedia(user!, SocialMedia.Youtube);

            var apiResponse = new ApiResponse($"Successfully removed your {SocialMedia.Youtube} account.");
            
            return new OkObjectResult(apiResponse);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }
    
    [HttpPut]
    [Route("@/youtube")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    public async Task<ActionResult<ApiResponse>> UpdateYoutube([FromQuery] string link)
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var linkValidation = _validation.ValidateYoutubeLink(link);

            if (!linkValidation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)linkValidation.ErrorCode!, linkValidation.Message!);
                return new ObjectResult(errorResponse){StatusCode = (int)HttpStatusCode.BadRequest};
            }

            await _service.UpdateYoutube(user!, link);

            var response = new ApiResponse("Successfully updated youtube link.");

            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
        
    }
    
    [HttpDelete]
    [Route("@/instagram")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> DeleteInstagram()
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email!);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var mediaValidation = _validation.ValidateSocialMediaForDelete(user!, SocialMedia.Instagram);
            if (!mediaValidation.Success)
            {
                var mediaErrorResponse = new ApiResponse((ErrorCode)mediaValidation.ErrorCode!, mediaValidation.Message!);
                return new ObjectResult(mediaErrorResponse){StatusCode = 404};
            }

            await _service.RemoveSocialMedia(user!, SocialMedia.Instagram);

            var apiResponse = new ApiResponse($"Successfully removed your {SocialMedia.Instagram} account.");
            
            return new OkObjectResult(apiResponse);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }
    
    [HttpPut]
    [Route("@/instagram")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    public async Task<ActionResult<ApiResponse>> UpdateInstagram([FromQuery] string link)
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }
            
            var linkValidation = _validation.ValidateInstagramLink(link);

            if (!linkValidation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)linkValidation.ErrorCode!, linkValidation.Message!);
                return new ObjectResult(errorResponse){StatusCode = (int)HttpStatusCode.BadRequest};
            }

            await _service.UpdateInstagram(user!, link);

            var response = new ApiResponse("Successfully updated instagram link.");

            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
        
    }

    [HttpPut]
    [Route("Update/Username/{username}")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse>> UpdateUsername([FromRoute] string username)
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email);
            
            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var uniqueValidation = await _validation.ValidateUniqueUsername(username);

            if (!uniqueValidation.Success)
            {
                if (uniqueValidation.ErrorCode == ErrorCode.InvalidEmail)
                {
                    var notVerifiedResponse = new ApiResponse(ErrorCode.InvalidEmail, uniqueValidation.Message!);
                    return new ObjectResult(notVerifiedResponse) { StatusCode = (int)HttpStatusCode.Conflict };
                }
                
                var uniqueErrorResponse = new ApiResponse((ErrorCode)uniqueValidation.ErrorCode!, uniqueValidation.Message!);
                return new ObjectResult(uniqueErrorResponse) { StatusCode = (int)HttpStatusCode.Conflict };
            }

            await _service.UpdateUsername(new UpdateUsernameDto
            {
                User = user!,      //valid user.
                Username = username
            });

            var apiResponse = new ApiResponse("Successfully Updated Username");

            return new ObjectResult(apiResponse);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    [HttpPut]
    [Route("Update/Password")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse>> UpdatePassword([FromBody] UpdatePasswordDto updatePasswordDto)
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email);
            
            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            await _service.UpdatePassword(updatePasswordDto, user!);

            var apiResponse = new ApiResponse("Successfully Updated Password!");

            return new OkObjectResult(apiResponse);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    [HttpPost]
    [Route("ForgotPassword")]
    public async Task<ActionResult<ApiResponse>> ForgotPassword([FromQuery] string email)
    {
        try
        {
            var (validation, user) = await _validation.ValidateUser(email);
            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var temporaryPassword = await _service.SetTemporaryPassword(user!);
            await _emailService.SendTemporaryPassword(email, temporaryPassword);

            var apiResponse = new ApiResponse("Check your email for your temporary password.");
            
            return new OkObjectResult(apiResponse);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    [HttpPost]
    [Route("UploadPhoto")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    public async Task<ActionResult<ApiResponse>> UploadPhoto([FromBody] UpdateUserPhotoDto updateUserPhotoDto)
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email!);
            
            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            await _service.UploadPhoto(user!, updateUserPhotoDto);

            var apiResponse = new ApiResponse("Successfully Added Photo Link to your Profile.");
            
            return new OkObjectResult(apiResponse);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    [HttpPost]
    [Route("Add-Artist-Role")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse>> AddArtistRole([FromQuery] string role)
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email!);
            
            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var roleValid = await _validation.ValidateUserRole(user!, role);
            if (!roleValid.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)roleValid.ErrorCode!, roleValid.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.Conflict };
            }

            await _service.AddRole(user!, role);
            
            var apiResponse = new ApiResponse("Successfully Added Role to your Profile.");
            
            return new OkObjectResult(apiResponse);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    [HttpPost]
    [Route("Remove-Artist-Role")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse>> RemoveArtistRole([FromQuery] string role)
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email!);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var roleValidation = await _validation.ValidateForRemoveRole(user!, role);
            if (!roleValidation.Success)
            {
                if (roleValidation.ErrorCode == ErrorCode.BadRequest)
                {
                    return new BadRequestObjectResult(new ApiResponse(ErrorCode.BadRequest, roleValidation.Message!));
                }

                return new ObjectResult(new ApiResponse(ErrorCode.NotFound, roleValidation.Message!)){StatusCode = (int)HttpStatusCode.Conflict};
            }

            await _service.RemoveRole(user!, role);

            var apiResponse = new ApiResponse("Successfully Removed the Role from your Profile.");
            
            return new OkObjectResult(apiResponse);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }
    
    [HttpPost]
    [Route("Set/Profile-Photo")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    public async Task<ActionResult<ApiResponse>> SetProfilePhoto([FromBody] SetProfilePhotoDto setProfilePhotoDto)
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email!);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }
            
            var (imageReport, userImage) = await _validation.ValidateUserImageExistence(setProfilePhotoDto.ImageId);

            if (!imageReport.Success)
            {
                var errorImageResponse = new ApiResponse((ErrorCode)imageReport.ErrorCode!, imageReport.Message!);
                return new ObjectResult(errorImageResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }
            
            var isOwner = _validation.ValidatePhotoOwnership(user!, userImage!);
            if (!isOwner.Success)
            {
                var ownershipError = new ApiResponse((ErrorCode)isOwner.ErrorCode!, isOwner.Message!);
                return new ObjectResult(ownershipError) { StatusCode = (int)HttpStatusCode.Forbidden };
            }

            await _service.SetProfilePhoto(user!, userImage!, setProfilePhotoDto);
            
            
            var apiResponse = new ApiResponse("Successfully Set Profile Photo.");
            
            return new OkObjectResult(apiResponse);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    [HttpPut]
    [Route("Unset/Profile-Photo/{imageId}")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    public async Task<ActionResult<ApiResponse>> UnsetProfilePhoto([FromRoute] int imageId)
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email!);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }
            
            var (imageReport, userImage) = await _validation.ValidateUserImageExistence(imageId);

            if (!imageReport.Success)
            {
                var errorImageResponse = new ApiResponse((ErrorCode)imageReport.ErrorCode!, imageReport.Message!);
                return new ObjectResult(errorImageResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }
            
            var isOwner = _validation.ValidatePhotoOwnership(user!, userImage!);
            if (!isOwner.Success)
            {
                var ownershipError = new ApiResponse((ErrorCode)isOwner.ErrorCode!, isOwner.Message!);
                return new ObjectResult(ownershipError) { StatusCode = (int)HttpStatusCode.Forbidden };
            }

            if (userImage!.IsProfile == false)
            {
                var prematureResponse = new ApiResponse(ErrorCode.BadRequest,"This image is not your profile picture");
                return new ObjectResult(prematureResponse){StatusCode = 400};
            }
            await _service.UnsetProfilePhoto(userImage!);
            
            var apiResponse = new ApiResponse("Successfully Unset your Profile Photo.");
            
            return new OkObjectResult(apiResponse);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    [HttpDelete]
    [Route("Remove/Image/{imageId}")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    public async Task<ActionResult<ApiResponse>> RemoveUserImage([FromRoute] int imageId)
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email!);

            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var (imageReport, userImage) = await _validation.ValidateUserImageExistence(imageId);

            if (!imageReport.Success)
            {
                var errorImageResponse = new ApiResponse((ErrorCode)imageReport.ErrorCode!, imageReport.Message!);
                return new ObjectResult(errorImageResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }
            
            var isOwner = _validation.ValidatePhotoOwnership(user!, userImage!);
            if (!isOwner.Success)
            {
                var ownershipError = new ApiResponse((ErrorCode)isOwner.ErrorCode!, isOwner.Message!);
                return new ObjectResult(ownershipError) { StatusCode = (int)HttpStatusCode.Forbidden };
            }
            
            await _service.RemoveUserImage(userImage!);
            
            var apiResponse = new ApiResponse("Successfully Removed Your Image.");
            
            return new OkObjectResult(apiResponse);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }

    [HttpPost]
    [Route("Upload/Bio")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    public async Task<ActionResult<ApiResponse>> UploadPdf([FromBody] UploadUserBioPdfDto uploadUserBioPdfDto)
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email!);
            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var fileLocation = await _minioService.PostUserBioPdf(uploadUserBioPdfDto.UserBioPdf, user!.Id);

            await _service.SetBio(user, fileLocation);

            var response = new ApiResponse($"Successfully Uploaded your Bio. " +
                                           $"You can see your bio here: {fileLocation}");
            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }
    
    [HttpPost]
    [Route("Unset/Bio")]
    [ServiceFilter(typeof(AnyRoleAuthorizationFilter))]
    public async Task<ActionResult<ApiResponse>> UnsetBio()
    {
        try
        {
            var email = HttpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var (validation, user) = await _validation.ValidateUser(email!);
            if (!validation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)validation.ErrorCode!, validation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }

            var bioValidation = _validation.ValidateBioExistence(user!);
            if (!bioValidation.Success)
            {
                var errorResponse = new ApiResponse((ErrorCode)bioValidation.ErrorCode!, bioValidation.Message!);
                return new ObjectResult(errorResponse) { StatusCode = (int)HttpStatusCode.NotFound };
            }
            
            await _service.UnsetBio(user!);

            var response = new ApiResponse("Successfully Set your Bio");
            return new OkObjectResult(response);
        }
        catch (Exception e)
        {
            var unexpectedResponse = new ApiResponse(ErrorCode.ServerError, e.Message);

            return new ObjectResult(unexpectedResponse){StatusCode = (int)HttpStatusCode.InternalServerError};
        }
    }
}