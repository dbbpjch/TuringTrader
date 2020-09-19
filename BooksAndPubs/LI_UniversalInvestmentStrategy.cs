﻿//==============================================================================
// Project:     TuringTrader, demo algorithms
// Name:        LI_UniversalInvestmentStrategy
// Description: Universal Investment Strategy as described by Logical Invest.
//              https://logical-invest.com/app/strategy/uis/universal-investment-strategy
//              https://logical-invest.com/universal-investment-strategy/
// History:     2020viiii15, FUB, created
//------------------------------------------------------------------------------
// Copyright:   (c) 2011-2020, Bertram Solutions LLC
//              https://www.bertram.solutions
// License:     This file is part of TuringTrader, an open-source backtesting
//              engine/ market simulator.
//              TuringTrader is free software: you can redistribute it and/or 
//              modify it under the terms of the GNU Affero General Public 
//              License as published by the Free Software Foundation, either 
//              version 3 of the License, or (at your option) any later version.
//              TuringTrader is distributed in the hope that it will be useful,
//              but WITHOUT ANY WARRANTY; without even the implied warranty of
//              MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
//              GNU Affero General Public License for more details.
//              You should have received a copy of the GNU Affero General Public
//              License along with TuringTrader. If not, see 
//              https://www.gnu.org/licenses/agpl-3.0.
//==============================================================================

// Implementation Note:
// For this implementation, we use TuringTrader's walk-forward-optimization.
// Some readers might feel that this is overkill, given the simplicity of the
// strategy. We agree that with a few lines of code a simple custom solution
// can be crafted, which is likely less CPU intense.
// However, we'd like to remind readers of the purpose of these showcase
// implementations. With these strategies, we want to show-off TuringTrader's
// features, and provide implementations which can serve as a robust starting
// point for your own experimentation. We have no doubt that our solution,
// based on walk-forward-optimization, scales better in these regards.

#region libraries
using System;
using System.Collections.Generic;
using System.Linq;
using TuringTrader.Algorithms.Glue;
using TuringTrader.Simulator;
#endregion

namespace TuringTrader.BooksAndPubs
{
    #region LI_UniversalInvestmentStrategy_Core
    public abstract class LI_UniversalInvestmentStrategy_Core : AlgorithmPlusGlue
    {
        #region inputs
        [OptimizerParam(0, 100, 5)]
        public virtual int VOL_FACT { get; set; } = 250;

        [OptimizerParam(50, 80, 5)]
        public virtual int LOOKBACK_DAYS { get; set; } = 72;

        [OptimizerParam(0, 100, 10)]
        public int STOCK_PCNT { get; set; } = 60;

        public abstract string STOCKS { get; }
        public abstract string BONDS { get; }

        public virtual string BENCHMARK => Assets.PORTF_60_40;
        #endregion
        #region internal helpers
        private double ModifiedSharpeRatio(double f)
        {
            // this code is only required for optimization
            if (!IsOptimizing)
                return 0.0;

            var dailyReturns = Enumerable.Range(0, TradingDays)
                .Select(t => Math.Log(NetAssetValue[t] / NetAssetValue[t + 1]))
                .ToList();
            var rd = dailyReturns.Average();
            var vd = dailyReturns
                .Select(r => Math.Pow(r - rd, 2.0))
                .Average();

            // modified sharpe ratio
            // f = 1.0: sharpe ratio
            // f = 0.0: only consider returns, not volatility
            // f > 1.0: increased relevance of volatility
            return rd / Math.Pow(vd, 0.5 * f);
        }
        private Dictionary<string, bool> SaveAndDisableOptimizerParams()
        {
            var isEnabled = new Dictionary<string, bool>();
            foreach (var s in OptimizerParams)
            {
                isEnabled[s.Key] = s.Value.IsEnabled;
                s.Value.IsEnabled = false;
            }
            return isEnabled;
        }

        private void RestoreOptimizerParams(Dictionary<string, bool> isEnabled)
        {
            foreach (var s in OptimizerParams)
                s.Value.IsEnabled = isEnabled[s.Key];
        }
        #endregion

        #region OptimizeParameter - walk-forward-optimization
        private void OptimizeParameter(string parameter)
        {
            if (parameter == "STOCK_PCNT"
                && !OptimizerParams["STOCK_PCNT"].IsEnabled)
            {
                var save = SaveAndDisableOptimizerParams();

                // run optimization
                var optimizer = new OptimizerGrid(this, false);
                var end = SimTime[0];
                var start = SimTime[LOOKBACK_DAYS];
                OptimizerParams["STOCK_PCNT"].IsEnabled = true;
                optimizer.Run(start, end);

                // apply parameters from best result
                var best = optimizer.Results
                    .OrderByDescending(r => r.Fitness)
                    .FirstOrDefault();
                optimizer.SetParametersFromResult(best);

                RestoreOptimizerParams(save);
            }

            // NOTE: Frank Grossmann does not mention optimization of the lookback period.
            // this code fragment is only meant to demonstrate
            // how we could expand optimzation to other parameters
            if (parameter == "LOOKBACK_DAYS"
                && !OptimizerParams["STOCK_PCNT"].IsEnabled
                && !OptimizerParams["LOOKBACK_DAYS"].IsEnabled)
            {
                // var save = SaveAndDisableOptimizerParams();
                // TODO: put optimizer code here
                // RestoreOptimizerParams(save);
            }
        }
        #endregion
        #region Run - algorithm core
        public override IEnumerable<Bar> Run(DateTime? startTime, DateTime? endTime)
        {
            //========== initialization ==========

            StartTime = startTime != null ? (DateTime)startTime : Globals.START_TIME;
            EndTime = endTime != null ? (DateTime)endTime : Globals.END_TIME;
            WarmupStartTime = StartTime - TimeSpan.FromDays(90);

            CommissionPerShare = Globals.COMMISSION;
            Deposit(Globals.INITIAL_CAPITAL);

            var stocks = AddDataSource(STOCKS);
            var bonds = AddDataSource(BONDS);
            var bench = AddDataSource(BENCHMARK);

            //========== simulation loop ==========

            bool firstOptimization = true;
            foreach (var s in SimTimes)
            {
                if (!HasInstruments(new List<DataSource> { stocks, bonds, bench }))
                    continue;

                if (SimTime[0] < StartTime)
                    continue;

#if false
                // NOTE: the Universal Investment Strategy does not
                // use walk-forward optimization for the lookback days.
                // this code is only meant to demonstrate how optimization
                // could be expanded to include more parameters.
                if (firstOptimization 
                || (NextSimTime.Month != SimTime[0].Month && new List<int> { 1, 7 }.Contains(NextSimTime.Month)))
                    OptimizeParameter("LOOKBACK_DAYS");
#endif

                // re-tune asset allocation on monthly schedule
                if (firstOptimization
                || NextSimTime.Month != SimTime[0].Month)
                    OptimizeParameter("STOCK_PCNT");

                firstOptimization = false;

                // open positions on first execution, rebalance monthly
                if (NextSimTime.Month != SimTime[0].Month || Positions.Count == 0)
                {
                    Alloc.LastUpdate = SimTime[0];

                    var stockPcnt = STOCK_PCNT / 100.0;
                    var stockShares = (int)Math.Floor(NetAssetValue[0] * stockPcnt / stocks.Instrument.Close[0]);
                    stocks.Instrument.Trade(stockShares - stocks.Instrument.Position);
                    Alloc.Allocation[stocks.Instrument] = stockPcnt;

                    var bondPcnt = 1.0 - stockPcnt;
                    var bondShares = (int)Math.Floor(NetAssetValue[0] * bondPcnt / bonds.Instrument.Close[0]);
                    bonds.Instrument.Trade(bondShares - bonds.Instrument.Position);
                    Alloc.Allocation[bonds.Instrument] = bondPcnt;
                }
                else
                {
                    Alloc.AdjustForPriceChanges(this);
                }

                // strategy output
                if (!IsOptimizing && TradingDays > 0)
                {
                    _plotter.AddNavAndBenchmark(this, bench.Instrument);
                    _plotter.AddStrategyHoldings(this, new List<Instrument> { stocks.Instrument, bonds.Instrument });

                    if (Alloc.LastUpdate == SimTime[0])
                        _plotter.AddTargetAllocationRow(Alloc);

                    //_plotter.SelectChart("Walk-Forward Optimization", "Date");
                    //_plotter.SetX(SimTime[0]);
                    //_plotter.Plot("STOCK_PCNT", STOCK_PCNT);
                    //_plotter.Plot("LOOKBACK_DAYS", LOOKBACK_DAYS);
                }

                var v = NetAssetValue[0] / Globals.INITIAL_CAPITAL;
                yield return Bar.NewOHLC(
                    this.GetType().Name, SimTime[0],
                    v, v, v, v, 0);
            }

            //========== post processing ==========

            if (!IsOptimizing)
            {
                _plotter.AddAverageHoldings(this);
                _plotter.AddTargetAllocation(Alloc);
                _plotter.AddOrderLog(this);
                _plotter.AddPositionLog(this);
                _plotter.AddPnLHoldTime(this);
                _plotter.AddMfeMae(this);
                _plotter.AddParameters(this);
            }

            // fitness value used for walk-forward-optimization
            FitnessValue = ModifiedSharpeRatio(VOL_FACT / 100.0);
        }
        #endregion
    }
    #endregion

    #region original SPY/ TLT
    public class LI_UniversalInvestmentStrategy : LI_UniversalInvestmentStrategy_Core
    {
        public override string Name => "Logical Invest's Universal Investment Strategy (SPY/ TLT)";

        public override string STOCKS => "SPY";

        // LogicalInvest uses HEDGE here
        public override string BONDS => "TLT";
    }
    #endregion
    #region 3x Leveraged 'Hell on Fire'
    public class LI_UniversalInvestmentStrategy_3x : LI_UniversalInvestmentStrategy_Core
    {
        public override string Name => "Logical Invest's Universal Investment Strategy (3x Leveraged 'Hell on Fire')";

        // LogicalInvest shorts the 3x inverse ETFs instead
        public override string STOCKS => Assets.STOCKS_US_LG_CAP_3X;

        public override string BONDS => Assets.BONDS_US_TREAS_30Y_3X;
    }
    #endregion

    #region more
#if false
    // NOTE: it is unclear if these strategies are built upon the
    // same UIS core. It is likely they are not, and use a simple
    // momentum-based switching method instead.

    // HEDGE: Hedge Strategy
    // https://logical-invest.com/app/strategy/hedge/hedge-strategy
    // GLD-UUP, TRESHEDGE

    // GLD-UUP: Gold-Currency
    // https://logical-invest.com/app/strategy/gld-uup/gold-currency-strategy-ii
    // GSY, GLD

    // TRESHEDGE: Treasury Hedge
    // https://logical-invest.com/app/strategy/treshedge/treasury-hedge
    // TLT, GSY, TIP
#endif
    #endregion
}

//==============================================================================
// end of file