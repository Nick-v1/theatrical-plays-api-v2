﻿using System.ComponentModel.DataAnnotations;

namespace Theatrical.Dto.UsersDtos;

public class RegisterUserDto
{
    [Required]
    public string Email { get; set; }
    
    [Required]
    public string Password { get; set; }

    public int? Role { get; set; } = 2;
}