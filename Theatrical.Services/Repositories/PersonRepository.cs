﻿using Microsoft.EntityFrameworkCore;
using Theatrical.Data.Context;
using Theatrical.Data.enums;
using Theatrical.Data.Models;
using Theatrical.Dto.AccountRequestDtos;
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
    Task<List<Image>?> GetImages();
    Task CreateRequest(Person person);
    Task ApproveRequest(RequestActionDto requestActionDto);
    Task RejectRequest(RequestActionDto requestActionDto);
    Task DeleteTestData();
    Task<List<Person>?> GetByNameRange(List<CreatePersonDto> persons);
    Task<List<Person>> CreateRange(List<Person> finalPeopleToAdd);
    Task SaveListChanges();
    Task<List<Person>> MapContributionRoleToPerson();
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
        var people = await _caching.GetOrSetAsync("allpeople", 
            async () => await _context.Persons
                .AsNoTracking()
                .Include(p => p.Images)
                .ToListAsync());
        
        return people;
    }
    
    public async Task<Person?> Get(int id)
    {
        var person = await _caching.GetOrSetAsync($"person_{id}", async () => await _context.Persons.Include(p => p.Images).FirstOrDefaultAsync(p => p.Id == id));
            
        return person;
    }

    /// <summary>
    /// Changes the claiming status to 1.
    /// </summary>
    /// <param name="person"></param>
    public async Task CreateRequest(Person person)
    {
        person.ClaimingStatus = ClaimingStatus.InProgress;
        await _context.SaveChangesAsync();
    }

    public async Task DeleteTestData()
    {
        var testPersons = await _context.Persons.Where(p => p.SystemId == 14).ToListAsync();
        _context.Persons.RemoveRange(testPersons);
        await _context.SaveChangesAsync();
    }

    public async Task ApproveRequest(RequestActionDto requestActionDto)
    {
        requestActionDto.Person.IsClaimed = true;
        requestActionDto.Person.ClaimingStatus = ClaimingStatus.Unavailable;

        await _context.SaveChangesAsync();
    }

    public async Task RejectRequest(RequestActionDto requestActionDto)
    {
        requestActionDto.Person.ClaimingStatus = ClaimingStatus.Available;
        await _context.SaveChangesAsync();
    }

    public async Task<List<Person>?> GetByRole(string role)
    {
        var people = await _caching.GetOrSetAsync($"persons_with_role_{role}", async () =>
        {
            return await _context.Persons
                .Include(p => p.Images)
                .Where(p => p.Contributions.Any(c => c.Role.Role1 == role))
                .ToListAsync();
        });

        return people;
    }

    public async Task<List<Person>> MapContributionRoleToPerson()
    {
        var contributionsWithRoles = await _context.Contributions
            .Include(c => c.Role)
            .ToListAsync();

        var peopleUpdated = new List<Person>();
        var fetchedPerson = new Dictionary<int, Person>();

        foreach (var contribution in contributionsWithRoles)
        {
            if (!fetchedPerson.TryGetValue(contribution.PersonId, out var person))
            {
                person = await _context.Persons.FindAsync(contribution.PersonId);
                
                fetchedPerson.Add(person.Id, person);
            }
                
            Console.WriteLine($"Person parsed: {person.Id}");

            if (person.Roles == null)
            {
                person.Roles = new List<string> { contribution.Role.Role1 };
                Console.WriteLine($"Created list role: {person.Id}, with first role: {contribution.Role.Id}");
                if (!peopleUpdated.Contains(person))
                {
                    peopleUpdated.Add(person);
                }
                continue;
            }
                
            if (!person.Roles.Contains(contribution.Role.Role1))
            {
                person.Roles.Add(contribution.Role.Role1);
                if (!peopleUpdated.Contains(person))
                {
                    peopleUpdated.Add(person);
                }
                Console.WriteLine($"Added new role for: {person.Id}");
            }
        }
        
        Console.WriteLine($"{peopleUpdated.Count} people had roles added.");
        await _context.SaveChangesAsync();
        return peopleUpdated;
    }

    public async Task<List<Person>?> GetByLetter(string initials)
    {
        var people = await _caching.GetOrSetAsync($"persons_by_initials_{initials}", async () =>
        {
            return await _context.Persons.Where(p => p.Fullname.StartsWith(initials)).Include(p => p.Images).ToListAsync();
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

    public async Task<List<Person>?> GetByNameRange(List<CreatePersonDto> persons)
    {
        var namesToMatch = persons.Select(dto => dto.Fullname).ToList();

        var matchingPersons = await _context.Persons.Where(p => namesToMatch.Contains(p.Fullname)).ToListAsync();

        return matchingPersons;
    }

    public async Task<List<Person>> CreateRange(List<Person> finalPeopleToAdd)
    {
        await _context.Persons.AddRangeAsync(finalPeopleToAdd);
        await _context.SaveChangesAsync();
        return finalPeopleToAdd;
    }

    public async Task SaveListChanges()
    {
        await _context.SaveChangesAsync();
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
                .Where(c => c.PersonId == personId)
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
    
    public async Task<List<Image>?> GetImages()
    {
        return await _caching.GetOrSetAsync($"all_images", async () =>
        {
            var images = await _context.Images.ToListAsync();
            
            return images;
        });
    }
}