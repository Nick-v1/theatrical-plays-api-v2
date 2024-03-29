﻿using Microsoft.EntityFrameworkCore;
using Theatrical.Data.Context;
using Theatrical.Data.Models;
using Theatrical.Dto.VenueDtos;
using Theatrical.Services.Caching;

namespace Theatrical.Services.Repositories;

public interface IVenueRepository
{
    Task<List<Venue>?> Get();
    Task<Venue?> Get(int id);
    Task<Venue> Create(Venue venue);
    Task Delete(Venue venue);
    Task<Venue> Update(Venue venue, VenueUpdateDto venueUpdateDto);
    Task<List<Venue>> UpdateRange(List<Venue> venues);
    Task<List<Production>?> GetVenueProductions(int venueId);
    Task<List<Venue>> GetVenuesByTitles(List<string> titles);
    Task<List<Venue>> CreateRange(List<Venue> venues);
    Task<Venue?> GetVenueByTitle(string venueTitle);
}

public class VenueRepository : IVenueRepository
{
    private readonly TheatricalPlaysDbContext _context;
    private readonly ILogRepository _logRepository;
    private readonly ICaching _caching;

    public VenueRepository(TheatricalPlaysDbContext context, ILogRepository logRepository, ICaching caching)
    {
        _context = context;
        _logRepository = logRepository;
        _caching = caching;
    }

    public async Task<List<Venue>?> Get()
    {
        var venues = await _caching.GetOrSetAsync("all_venues",async() => await _context.Venues.ToListAsync());
        return venues;
    }

    public async Task<Venue?> Get(int id)
    {
        var venue = await _caching.GetOrSetAsync($"venue_{id}", async () => await _context.Venues.FindAsync(id));
        return venue;
    }
    
    public async Task<List<Production>?> GetVenueProductions(int venueId)
    {
        var venueProductions = await _caching.GetOrSetAsync($"venue_{venueId}_productions", async () =>
        {
            return await _context.Events
                .Where(e => e.VenueId == venueId)
                .Select(e => e.Production)
                .ToListAsync();
        });
        
        return venueProductions;
    }

    public async Task<List<Venue>> GetVenuesByTitles(List<string> titles)
    {
        return await _context.Venues.Where(venue => venue.Title != null && titles.Contains(venue.Title)).ToListAsync();
    }

    public async Task<Venue> Create(Venue venue)
    {
        await _context.Venues.AddAsync(venue);
        await _context.SaveChangesAsync();

        var columns = new List<(string ColumnName, string Value)>
        {
            ("ID", venue.Id.ToString())
        };

        if (venue.Title != null)
        {
            columns.Add(("Title", venue.Title));
        }

        if (venue.Address != null)
        {
            columns.Add(("Address", venue.Address));
        }

        await _logRepository.UpdateLogs("insert", "venue", columns);

        return venue;
    }

    public async Task Delete(Venue venue)
    {
        _context.Venues.Remove(venue);
        await _context.SaveChangesAsync();
        
        var columns = new List<(string ColumnName, string Value)>
        {
            ("ID", venue.Id.ToString())
        };

        if (venue.Title != null)
        {
            columns.Add(("Title", venue.Title));
        }

        if (venue.Address != null)
        {
            columns.Add(("Address", venue.Address));
        }

        await _logRepository.UpdateLogs("delete", "venue", columns);
    }

    public async Task<Venue> Update(Venue venue, VenueUpdateDto venueUpdateDto)
    {
        if (venueUpdateDto.Title != null) venue.Title = venueUpdateDto.Title;
        if (venueUpdateDto.Address != null) venue.Address = venueUpdateDto.Address;
        await _context.SaveChangesAsync();
        /*await _logRepository.UpdateLogs("update", "venue", new List<(string ColumnName, string Value)>
        {
            ("ID", venue.Id.ToString()),
            ("Title", venue.Title),
            ("Address", venue.Address)
        });*/
        return venue;
    }

    public async Task<List<Venue>> UpdateRange(List<Venue> venues)
    {
        _context.Venues.UpdateRange(venues);
        await _context.SaveChangesAsync();
        return venues;
    }

    public async Task<List<Venue>> CreateRange(List<Venue> venues)
    {
        await _context.Venues.AddRangeAsync(venues);
        await _context.SaveChangesAsync();
        return venues;
    }

    public async Task<Venue?> GetVenueByTitle(string venueTitle)
    {
        var uppercasedVenueTitle = venueTitle.ToUpper();
        
        var venue = await _context.Venues
            .Where(v => v.Title != null && v.Title.ToUpper() == uppercasedVenueTitle)
            .FirstOrDefaultAsync();

        return venue;
    }
}