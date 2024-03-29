﻿namespace Theatrical.Data.Models;

public class UserAuthority
{
    public int UserId { get; set; }
    public int AuthorityId { get; set; }
    
    //Navigational Properties
    public virtual User User { get; set; }
    public virtual Authority Authority { get; set; }
}