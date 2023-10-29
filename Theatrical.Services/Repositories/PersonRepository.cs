﻿using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Theatrical.Data.Context;
using Theatrical.Data.Models;
using Theatrical.Dto.PersonDtos;
using Theatrical.Services.Caching;

namespace Theatrical.Services.Repositories;

public interface IPersonRepository
{
    Task<Person> Create(Person person);
    Task<List<Person>> Get();
    Task Delete(Person person);
    Task<Person?> Get(int id);
    Task<List<Person>?> GetByRole(string role);
    Task<List<Person>?> GetByLetter(string initials);
    Task<Person?> GetByName(string name);
    Task<List<PersonProductionsRoleInfo>?> GetProductionsOfPerson(int personId);
    Task<List<Image>?> GetPersonsImages(int personId);
    Task UpdateRange(List<Person> people);
}

public class PersonRepository : IPersonRepository
{
    private readonly TheatricalPlaysDbContext _context;
    private readonly ICaching _caching;

    public PersonRepository(TheatricalPlaysDbContext context, ICaching caching)
    {
        _context = context;
        _caching = caching;
    }

    public async Task<Person> Create(Person person)
    {
        await _context.Persons.AddAsync(person);
        await _context.SaveChangesAsync();
        return person;
    }
    
    public async Task<List<Person>> Get()
    {
        var people = await _caching.GetOrSetAsync("allpeople", async () => await _context.Persons.ToListAsync());

        return people;
    }
    
    public async Task<Person?> Get(int id)
    {
        var person = await _caching.GetOrSetAsync($"person_{id}", async () => await _context.Persons.FindAsync(id));
            
        return person;
    }

    public async Task<List<Person>?> GetByRole(string role)
    {
        var people = await _caching.GetOrSetAsync($"persons_with_role_{role}", async () =>
        {
            return await _context.Persons
                .Where(p => p.Contributions.Any(c => c.Role.Role1 == role))
                .ToListAsync();
        });

        return people;
    }

    public async Task<List<Person>?> GetByLetter(string initials)
    {
        var people = await _caching.GetOrSetAsync($"persons_by_initials_{initials}", async () =>
        {
            return await _context.Persons.Where(p => p.Fullname.StartsWith(initials)).ToListAsync();
        });
        
        
        return people;
    }

    public async Task<Person?> GetByName(string name)
    {
        var person = await _caching.GetOrSetAsync($"person_by_name_{name}", async () =>
        {
            return await _context.Persons.FirstOrDefaultAsync(p => p.Fullname == name);
        });

        return person;
    }

    public async Task Delete(Person person)
    {
        _context.Persons.Remove(person);
        await _context.SaveChangesAsync();
    }

    public async Task<List<PersonProductionsRoleInfo>?> GetProductionsOfPerson(int personId)
    {
        
        return await _caching.GetOrSetAsync($"person_productions_{personId}", async () =>
        {
            var personProductions = await _context.Contributions
                .Where(c => c.PeopleId == personId)
                .Include(c => c.Role)
                .Select(c => new PersonProductionsRoleInfo
                {
                    Production = c.Production,
                    Role = c.Role
                })
                .ToListAsync();

            return personProductions;
        });
        
    }

    public async Task<List<Image>?> GetPersonsImages(int personId)
    {
        return await _caching.GetOrSetAsync($"person_images_{personId}", async () =>
        {
            var personImages = await _context.Persons
                .Where(p => p.Id == personId)
                .Include(p => p.Images)
                .SelectMany(p => p.Images)
                .ToListAsync();
            return personImages;
        });
    }

    public async Task UpdateRange(List<Person> people)
    {
        _context.Persons.UpdateRange(people);
        await _context.SaveChangesAsync();
    }
}