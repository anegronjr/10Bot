﻿using System;
using System.Collections.Generic;
using System.Text;
using _10Bot.Models;
using Discord;
using Discord.WebSocket;
using System.Linq;
using _10Bot.Glicko2;

namespace _10Bot.Classes
{
    public class GameLobby
    {
        public enum LobbyState
        {
            Queuing,
            PickingPlayers,
            Reporting,
            Complete
        }
        public int ID { get; set; }
        public List<User> Players { get; set; }
        public List<User> RemainingPlayers { get; set; }
        public List<User> Team1 { get; set; }
        public List<User> Team2 { get; set; }
        public User Captain1 { get; set; }
        public User Captain2 { get; set; }
        public int PickTurn { get; set; }
        public Map Map { get; set; }
        public List<User> WinningTeam { get; set; }
        public bool AwaitingConfirmation { get; set; }
        public LobbyState State { get; set; }

        private readonly EFContext db;
        private readonly Random random;

        public GameLobby()
        {
            db = new EFContext();
            this.random = new Random();

            Players = new List<User>();
            Team1 = new List<User>();
            Team2 = new List<User>();
            AwaitingConfirmation = false;
        }

        public void PopQueue()
        {
            ChooseCaptains();
            ChooseMap();

            //Set remaining players.
            RemainingPlayers = Players.ToList();
            RemainingPlayers.Remove(Captain1);
            RemainingPlayers.Remove(Captain2);

            //Set pick turn and game state.
            PickTurn = 1;
            State = LobbyState.PickingPlayers;
        }

        private void ChooseCaptains()
        {
            //Try to find captains with at least 15 games played...
            var captainPool = Players.Where(p => (p.Wins + p.Losses) >= 15);
            if (captainPool.Count() < 2)
                captainPool = Players;

            //Find at most five of the highest rated players in the Captain pool.
            int num = Math.Min(captainPool.Count(), 5);
            var orderList = captainPool.OrderByDescending(c => c.SkillRating).Take(num);

            //First captain is randomly chosen.
            int capt1Index = random.Next(orderList.Count());
            Captain1 = orderList.ToList()[capt1Index];

            //Second captain is the player closest in rating to the first captain.
            int indexAbove = capt1Index - 1;
            int indexBelow = capt1Index + 1;

            double skillDiffAbove = double.MaxValue;
            double skillDiffBelow = double.MaxValue;

            var playerAbove = orderList.ElementAtOrDefault(indexAbove);
            var playerBelow = orderList.ElementAtOrDefault(indexBelow);

            if (playerAbove != null)
                skillDiffAbove = Math.Abs(Captain1.SkillRating - playerAbove.SkillRating);
            if (playerBelow != null)
                skillDiffBelow = Math.Abs(Captain1.SkillRating - playerBelow.SkillRating);

            if (skillDiffAbove < skillDiffBelow)
                Captain2 = playerAbove;
            else
                Captain2 = playerBelow;

            //Make sure Captain with lower rating has first pick.
            if (Captain1.SkillRating > Captain2.SkillRating)
            {
                var temp = Captain1;
                Captain1 = Captain2;
                Captain2 = temp;
            }

            //Add captains to their respective teams.
            Team1.Add(Captain1);
            Team2.Add(Captain2);
        }

        private void ChooseMap()
        {
            var maps = db.Maps.ToList();
            var index = random.Next(maps.Count);

            Map = maps[index];
        }

        public void ResetReport()
        {
            WinningTeam = null;
            AwaitingConfirmation = false;
            Captain1.HasAlreadyReported = false;
            Captain2.HasAlreadyReported = false;
        }

        public void ReportResult()
        {
            UpdatePlayerRatings();
            UpdateWinLosses();

            State = LobbyState.Complete;
        }

        private void UpdatePlayerRatings()
        {
            //foreach (var player in Players)
            //{
            //    player.PreviousSkillRating = player.SkillRating;
            //}


            var calculator = new RatingCalculator(/* initVolatility, tau */);

            // Instantiate a RatingPeriodResults object.
            var results = new RatingPeriodResults();

            var ratingsPlayers = new List<Tuple<User, Rating>>();

            double team1Rating = Team1.Sum(x => x.SkillRating) / Team1.Count;
            double team2Rating = Team2.Sum(x => x.SkillRating) / Team2.Count;

            double team1RatingsDeviation = Team1.Sum(x => x.RatingsDeviation) / Team1.Count;
            double team2RatingsDeviation = Team2.Sum(x => x.RatingsDeviation) / Team2.Count;

            double team1Volatility = Team1.Sum(x => x.Volatility) / Team1.Count;
            double team2Volatility = Team2.Sum(x => x.Volatility) / Team2.Count;

            var team1RatingCalc = new Rating(calculator, team1Rating, team1RatingsDeviation, team1Volatility);
            var team2RatingCalc = new Rating(calculator, team2Rating, team2RatingsDeviation, team2Volatility);

            foreach (var player in Team1)
            {
                var playerRating = new Rating(calculator, player.SkillRating, player.RatingsDeviation, player.Volatility);

                ratingsPlayers.Add(new Tuple<User, Rating>(player, playerRating));

                if (WinningTeam == Team1)
                {
                    results.AddResult(playerRating, team2RatingCalc);
                }
                else if (WinningTeam == Team2)
                {
                    results.AddResult(team2RatingCalc, playerRating);
                }
            }

            foreach (var player in Team2)
            {
                var playerRating = new Rating(calculator, player.SkillRating, player.RatingsDeviation, player.Volatility);

                ratingsPlayers.Add(new Tuple<User, Rating>(player, playerRating));

                if (WinningTeam == Team1)
                {
                    results.AddResult(team1RatingCalc, playerRating);
                }
                else if (WinningTeam == Team2)
                {
                    results.AddResult(playerRating, team1RatingCalc);
                }
            }

            calculator.UpdateRatings(results);

            foreach (var player in ratingsPlayers)
            {
                player.Item1.PreviousSkillRating = player.Item1.SkillRating;
                player.Item1.SkillRating = player.Item2.GetRating();
                player.Item1.RatingsDeviation = player.Item2.GetRatingDeviation();
                player.Item1.Volatility = player.Item2.GetVolatility();

                db.Users.Update(player.Item1);
            }

            db.SaveChanges();
        }

        private void UpdateWinLosses()
        {
            if (WinningTeam == Team1)
            {
                foreach (var player in Team1)
                    player.Wins++;
                foreach (var player in Team2)
                    player.Losses++;
            }
            else
            {
                foreach (var player in Team1)
                    player.Losses++;
                foreach (var player in Team2)
                    player.Wins++;
            }

            foreach (var player in Players)
                db.Users.Update(player);

            db.SaveChanges();
        }
    }
}
