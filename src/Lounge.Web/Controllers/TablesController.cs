﻿using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Collections.Generic;
using Lounge.Web.Models;
using Lounge.Web.Models.ViewModels;
using Lounge.Web.Utils;
using Lounge.Web.Data;
using System;
using Microsoft.AspNetCore.Authorization;

namespace Lounge.Web.Controllers
{
    [Route("api/table")]
    [Authorize]
    [ApiController]
    public class TablesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public TablesController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<ActionResult<TableDetailsViewModel>> GetTable(int tableId)
        {
            var table = await _context.Tables
                .Include(t => t.Scores)
                .ThenInclude(s => s.Player)
                .AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == tableId);

            if (table is null)
                return NotFound();

            return TableUtils.GetTableDetails(table);
        }

        [HttpGet("list")]
        public async Task<ActionResult<List<TableDetailsViewModel>>> GetTables(DateTime from, DateTime? to)
        {
            var tables = await _context.Tables
                .Include(t => t.Scores)
                .ThenInclude(s => s.Player)
                .AsNoTracking()
                .Where(t => t.CreatedOn >= from && (to == null || t.CreatedOn <= to))
                .ToListAsync();

            return tables.Select(TableUtils.GetTableDetails).ToList();
        }

        [HttpGet("unverified")]
        public async Task<ActionResult<List<TableDetailsViewModel>>> GetUnverifiedTables()
        {
            var tables = await _context.Tables
                .Include(t => t.Scores)
                .ThenInclude(s => s.Player)
                .AsNoTracking()
                .Where(t => t.VerifiedOn == null)
                .ToListAsync();

            return tables.Select(TableUtils.GetTableDetails).ToList();
        }

        [HttpPost("create")]
        public async Task<ActionResult<TableDetailsViewModel>> Create(NewTableViewModel vm)
        {
            if (vm.Scores.Count != 12)
                return BadRequest("Must supply 12 scores");

            var playerNames = vm.Scores.Select(s => s.PlayerName).ToHashSet();
            var normalizedPlayerNames = playerNames.Select(PlayerUtils.NormalizeName).ToHashSet();
            if (playerNames.Count != vm.Scores.Count)
                return BadRequest("Duplicate player name in scores");

            var players = await _context.Players.Where(p => normalizedPlayerNames.Contains(p.NormalizedName)).ToListAsync();
            if (players.Count != playerNames.Count)
            {
                var foundPlayers = players.Select(p => p.NormalizedName).ToHashSet();
                var invalidPlayers = playerNames.Where(name => !foundPlayers.Contains(PlayerUtils.NormalizeName(name))).ToArray();
                return NotFound($"Invalid players: {string.Join(", ", invalidPlayers)}");
            }

            var playerIdLookup = players.ToDictionary(p => p.Name, p => p.Id);

            int numTeams = vm.Scores.Max(s => s.Team) + 1;
            if (numTeams is not (2 or 3 or 4 or 6 or 12))
                return BadRequest("Invalid number of teams");

            int playersPerTeam = 12 / numTeams;

            var tableScores = new List<TableScore>();
            foreach (var score in vm.Scores)
            {
                tableScores.Add(new TableScore
                {
                    PlayerId = playerIdLookup[score.PlayerName],
                    Score = score.Score,
                    Team = score.Team,
                    Multiplier = score.Multiplier
                });
            }

            var scores = new (string Player, int Score)[numTeams][];
            for (int i = 0; i < numTeams; i++)
            {
                scores[i] = vm.Scores
                    .Where(score => score.Team == i)
                    .Select(score => (score.PlayerName, score.Score))
                    .ToArray();

                if (scores[i].Length != playersPerTeam)
                    return BadRequest($"Invalid number of players on team {i}: got {scores[i].Length}, expected {playersPerTeam}");
            }

            string tableUrl = TableUtils.BuildUrl(vm.Tier, scores);
            string dataUrl = await TableUtils.GetImageAsBase64UrlAsync(tableUrl);

            var table = new Table
            {
                CreatedOn = DateTime.UtcNow,
                NumTeams = numTeams,
                Url = tableUrl,
                Tier = vm.Tier,
                Scores = tableScores,
                TableImageData = dataUrl,
            };

            await _context.Tables.AddAsync(table);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetTable), new { tableId = table.Id }, TableUtils.GetTableDetails(table));
        }

        [HttpPost("setMultipliers")]
        public async Task<IActionResult> SetMultipliers(int tableId, [FromBody] Dictionary<string, double> multipliers)
        {
            var table = await _context.Tables
                .Include(t => t.Scores)
                .ThenInclude(t => t.Player)
                .FirstOrDefaultAsync(t => t.Id == tableId);

            if (table is null)
                return NotFound();

            if (table.VerifiedOn is not null)
                return BadRequest("Table has already been verified");

            foreach ((string name, double multiplier) in multipliers)
            {
                bool foundPlayer = false;
                foreach (var score in table.Scores)
                {
                    if (PlayerUtils.NormalizeName(name) == score.Player.NormalizedName)
                    {
                        foundPlayer = true;
                        score.Multiplier = multiplier;
                        break;
                    }
                }

                if (!foundPlayer)
                    return BadRequest($"Player '{name}' is not in table");
            }

            await _context.SaveChangesAsync();

            return Ok();
        }

        [HttpPost("setTableMessageId")]
        public async Task<IActionResult> SetTableMessageId(int tableId, int tableMessageId)
        {
            var table = await _context.Tables.FirstOrDefaultAsync(t => t.Id == tableId);

            if (table is null)
                return NotFound();

            table.TableMessageId = tableMessageId;

            await _context.SaveChangesAsync();

            return Ok();
        }

        [HttpPost("setUpdateMessageId")]
        public async Task<IActionResult> SetUpdateMessageId(int tableId, int updateMessageId)
        {
            var table = await _context.Tables.FirstOrDefaultAsync(t => t.Id == tableId);

            if (table is null)
                return NotFound();

            table.UpdateMessageId = updateMessageId;

            await _context.SaveChangesAsync();

            return Ok();
        }

        [HttpPost("verify")]
        public async Task<ActionResult<TableDetailsViewModel>> Verify(int tableId, bool preview=false)
        {
            var table = await _context.Tables
                .Include(t => t.Scores)
                .ThenInclude(t => t.Player)
                .FirstOrDefaultAsync(t => t.Id == tableId);

            if (table is null)
                return NotFound();

            if (table.VerifiedOn is not null)
                return BadRequest("Table has already been verified");

            if (table.DeletedOn is not null)
                return BadRequest("Table has been deleted and can't be verified");

            int numTeams = table.NumTeams;

            var unplacedPlayers = table.Scores.Where(s => s.Player.Mmr == null).Select(s => s.Player.Name).ToArray();
            if (unplacedPlayers.Any())
                return BadRequest($"The following players have not been placed yet: {string.Join(", ", unplacedPlayers)}");

            var scores = new (string Player, int Score, int CurrentMmr, double Multiplier)[numTeams][];
            for (int i = 0; i < numTeams; i++)
                scores[i] = table.Scores.Where(score => score.Team == i).Select(s => (s.Player.Name, s.Score, s.Player.Mmr!.Value, s.Multiplier)).ToArray();

            var mmrDeltas = TableUtils.GetMMRDeltas(scores);
            foreach (var score in table.Scores)
            {
                var delta = mmrDeltas[score.Player.Name];
                int prevMmr = score.Player.Mmr!.Value;
                int newMmr = prevMmr + delta;
                score.PrevMmr = prevMmr;
                score.NewMmr = newMmr;

                score.Player.Mmr = newMmr;
                score.Player.MaxMmr = Math.Max(score.Player.MaxMmr!.Value, newMmr);
            }

            if (!preview)
            {
                table.VerifiedOn = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }

            var vm = TableUtils.GetTableDetails(table);
            return Ok(vm);
        }

        [HttpDelete]
        public async Task<IActionResult> Delete(int tableId)
        {
            var table = await _context.Tables
                .Include(t => t.Scores)
                .ThenInclude(t => t.Player)
                .FirstOrDefaultAsync(t => t.Id == tableId);

            if (table is null)
                return NotFound();

            if (table.DeletedOn is not null)
                return BadRequest("Table has already been deleted");

            table.DeletedOn = DateTime.UtcNow;

            if (table.VerifiedOn is not null)
            {
                foreach (var score in table.Scores)
                {
                    var curMMR = score.Player.Mmr!.Value;
                    var diff = score.NewMmr!.Value - score.PrevMmr!.Value;
                    var newMMR = Math.Max(0, curMMR - diff);
                    score.Player.Mmr = newMMR;
                    score.Player.MaxMmr = Math.Max(score.Player.MaxMmr!.Value, newMMR);
                }
            }

            await _context.SaveChangesAsync();

            return Ok();
        }
    }
}
