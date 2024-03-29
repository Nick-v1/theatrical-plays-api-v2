﻿using Microsoft.EntityFrameworkCore;
using Theatrical.Data.Context;
using Theatrical.Data.Models;
using Theatrical.Services.Caching;

namespace Theatrical.Services.Repositories;

public interface IProductionRepository
{
   Task Create(Production production);
   Task<List<Production>?> Get();
   Task<Production?> GetProduction(int id);
   Task Delete(Production production);
   Task<List<Production>> UpdateRange(List<Production> productions);
   Task<List<Production>> GetProductionsByTitles(List<string> productionsTitles);
   Task<List<Production>> CreateRange(List<Production> productions);
}

public class ProductionRepository : IProductionRepository
{
    private readonly TheatricalPlaysDbContext _context;
    private readonly ICaching _caching;

    public ProductionRepository(TheatricalPlaysDbContext context, ICaching caching)
    {
        _context = context;
        _caching = caching;
    }

    public async Task Create(Production production)
    {
        await _context.Productions.AddAsync(production);
        await _context.SaveChangesAsync();
    }

    public async Task<Production?> GetProduction(int id)
    {
        var production = await _context.Productions.FindAsync(id);
        return production;
    }

    public async Task<List<Production>?> Get()
    {
        var productions = await _caching.GetOrSetAsync("all_productions", async () => await _context.Productions.ToListAsync());
        
        return productions;
    }

    public async Task Delete(Production production)
    {
        _context.Productions.Remove(production);
        await _context.SaveChangesAsync();
    }

    public async Task<List<Production>> UpdateRange(List<Production> productions)
    {
        _context.Productions.UpdateRange(productions);
        await _context.SaveChangesAsync();
        return productions;
    }

    public async Task<List<Production>> CreateRange(List<Production> productions)
    {
        await _context.Productions.AddRangeAsync(productions);
        await _context.SaveChangesAsync();
        return productions;
    }

    public async Task<List<Production>> GetProductionsByTitles(List<string> productionsTitles)
    {
        return await _context.Productions.Where(p => productionsTitles.Contains(p.Title)).ToListAsync();
    }
}
