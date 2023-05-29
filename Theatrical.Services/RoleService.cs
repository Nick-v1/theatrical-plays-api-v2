﻿using Theatrical.Data.Models;
using Theatrical.Services.Repositories;

namespace Theatrical.Services;

public interface IRoleService
{
    Task Create(string role);
    Task Delete(Role role);
}

public class RoleService : IRoleService
{
    private readonly IRoleRepository _repository;

    public RoleService(IRoleRepository repository)
    {
        _repository = repository;
    }

    public async Task Create(string role)
    {
        var _role = new Role();
        _role.Value = role;
        await _repository.CreateRole(_role);
    }

    public async Task Delete(Role role)
    {
        await _repository.DeleteRole(role);
    }
}