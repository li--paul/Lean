﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using NodaTime;
using NodaTime.TimeZones;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Data.Fundamental;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Notifications;
using QuantConnect.Orders;
using QuantConnect.Scheduling;
using QuantConnect.Securities;
using QuantConnect.Statistics;

namespace QuantConnect.Algorithm
{
    /// <summary>
    /// QC Algorithm Base Class - Handle the basic requirements of a trading algorithm, 
    /// allowing user to focus on event methods. The QCAlgorithm class implements Portfolio, 
    /// Securities, Transactions and Data Subscription Management.
    /// </summary>
    public partial class QCAlgorithm : MarshalByRefObject, IAlgorithm
    {
        private readonly TimeKeeper _timeKeeper;
        private LocalTimeKeeper _localTimeKeeper;

        private DateTime _startDate;   //Default start and end dates.
        private DateTime _endDate;     //Default end to yesterday
        private RunMode _runMode = RunMode.Series;
        private bool _locked;
        private bool _quit;
        private bool _liveMode;
        private string _algorithmId = "";
        private List<string> _debugMessages = new List<string>();
        private List<string> _logMessages = new List<string>();
        private List<string> _errorMessages = new List<string>();
        
        //Error tracking to avoid message flooding:
        private string _previousDebugMessage = "";
        private string _previousErrorMessage = "";
        private bool _sentNoDataError = false;

        private readonly SecurityExchangeHoursProvider _exchangeHoursProvider;

        // used for calling through to void OnData(Slice) if no override specified
        private bool _checkedForOnDataSlice;
        private Action<Slice> _onDataSlice;

        // set by SetBenchmark helper API functions
        private Symbol _benchmarkSymbol = Symbol.Empty;
        private SecurityType _benchmarkSecurityType;

        /// <summary>
        /// QCAlgorithm Base Class Constructor - Initialize the underlying QCAlgorithm components.
        /// QCAlgorithm manages the transactions, portfolio, charting and security subscriptions for the users algorithms.
        /// </summary>
        public QCAlgorithm()
        {
            //Initialise the Algorithm Helper Classes:
            //- Note - ideally these wouldn't be here, but because of the DLL we need to make the classes shared across 
            //  the Worker & Algorithm, limiting ability to do anything else.

            //Initialise Start and End Dates:
            _startDate = new DateTime(1998, 01, 01);
            _endDate = DateTime.Now.AddDays(-1);

            // intialize our time keeper with only new york
            _timeKeeper = new TimeKeeper(_startDate, new[] { TimeZones.NewYork });
            // set our local time zone
            _localTimeKeeper = _timeKeeper.GetLocalTimeKeeper(TimeZones.NewYork);

            //Initialise Data Manager 
            SubscriptionManager = new SubscriptionManager(_timeKeeper);

            Securities = new SecurityManager(_timeKeeper);
            Transactions = new SecurityTransactionManager(Securities);
            Portfolio = new SecurityPortfolioManager(Securities, Transactions);
            BrokerageModel = new DefaultBrokerageModel();
            Notify = new NotificationManager(false); // Notification manager defaults to disabled.

            //Initialise Algorithm RunMode to Series - Parallel Mode deprecated:
            _runMode = RunMode.Series;

            //Initialise to unlocked:
            _locked = false;

            // get exchange hours loaded from the market-hours-database.csv in /Data/market-hours
            _exchangeHoursProvider = SecurityExchangeHoursProvider.FromDataFolder();

            UniverseSettings = new SubscriptionSettings(Resolution.Minute, 2m, true, false);

            // initialize our scheduler, this acts as a liason to the real time handler
            Schedule = new ScheduleManager(Securities, TimeZone);

            // initialize the trade builder
            TradeBuilder = new TradeBuilder(FillGroupingMethod.FillToFill, FillMatchingMethod.FIFO);
        }

        /// <summary>
        /// Security collection is an array of the security objects such as Equities and FOREX. Securities data 
        /// manages the properties of tradeable assets such as price, open and close time and holdings information.
        /// </summary>
        public SecurityManager Securities
        { 
            get; 
            set; 
        }

        /// <summary>
        /// Portfolio object provieds easy access to the underlying security-holding properties; summed together in a way to make them useful.
        /// This saves the user time by providing common portfolio requests in a single 
        /// </summary>
        public SecurityPortfolioManager Portfolio 
        { 
            get; 
            set; 
        }


        /// <summary>
        /// Generic Data Manager - Required for compiling all data feeds in order, and passing them into algorithm event methods.
        /// The subscription manager contains a list of the data feed's we're subscribed to and properties of each data feed.
        /// </summary>
        public SubscriptionManager SubscriptionManager 
        { 
            get; 
            set; 
        }


        /// <summary>
        /// Gets the brokerage model - used to model interactions with specific brokerages.
        /// </summary>
        public IBrokerageModel BrokerageModel
        {
            get; 
            set;
        }

        /// <summary>
        /// Notification Manager for Sending Live Runtime Notifications to users about important events.
        /// </summary>
        public NotificationManager Notify
        {
            get; 
            set;
        }

        /// <summary>
        /// Gets schedule manager for adding/removing scheduled events
        /// </summary>
        public ScheduleManager Schedule
        {
            get; 
            private set;
        }

        /// <summary>
        /// Gets or sets the history provider for the algorithm
        /// </summary>
        IHistoryProvider IAlgorithm.HistoryProvider
        {
            get;
            set;
        }

        /// <summary>
        /// Gets the Trade Builder to generate trades from executions
        /// </summary>
        public TradeBuilder TradeBuilder { get; private set; }

        /// <summary>
        /// Gets the date rules helper object to make specifying dates for events easier
        /// </summary>
        public DateRules DateRules
        {
            get { return Schedule.DateRules; }
        }

        /// <summary>
        /// Gets the time rules helper object to make specifying times for events easier
        /// </summary>
        public TimeRules TimeRules
        {
            get { return Schedule.TimeRules; }
        }

        /// <summary>
        /// Public name for the algorithm as automatically generated by the IDE. Intended for helping distinguish logs by noting 
        /// the algorithm-id.
        /// </summary>
        /// <seealso cref="AlgorithmId"/>
        public string Name 
        {
            get;
            set;
        }


        /// <summary>
        /// Read-only value for current time frontier of the algorithm in terms of the <see cref="TimeZone"/>
        /// </summary>
        /// <remarks>During backtesting this is primarily sourced from the data feed. During live trading the time is updated from the system clock.</remarks>
        public DateTime Time
        {
            get { return _localTimeKeeper.LocalTime; }
        }

        /// <summary>
        /// Current date/time in UTC.
        /// </summary>
        public DateTime UtcTime
        {
            get { return _timeKeeper.UtcTime; }
        }

        /// <summary>
        /// Gets the time zone used for the <see cref="Time"/> property. The default value
        /// is <see cref="TimeZones.NewYork"/>
        /// </summary>
        public DateTimeZone TimeZone
        {
            get {  return _localTimeKeeper.TimeZone; }
        }

        /// <summary>
        /// Value of the user set start-date from the backtest. 
        /// </summary>
        /// <remarks>This property is set with SetStartDate() and defaults to the earliest QuantConnect data available - Jan 1st 1998. It is ignored during live trading </remarks>
        /// <seealso cref="SetStartDate(DateTime)"/>
        public DateTime StartDate 
        {
            get 
            {
                return _startDate;
            }
        }

        /// <summary>
        /// Value of the user set start-date from the backtest. Controls the period of the backtest.
        /// </summary>
        /// <remarks> This property is set with SetEndDate() and defaults to today. It is ignored during live trading.</remarks>
        /// <seealso cref="SetEndDate(DateTime)"/>
        public DateTime EndDate 
        {
            get 
            {
                return _endDate;
            }
        }

        /// <summary>
        /// Algorithm Id for this backtest or live algorithm. 
        /// </summary>
        /// <remarks>A unique identifier for </remarks>
        public string AlgorithmId 
        {
            get 
            {
                return _algorithmId;
            }
        }

        /// <summary>
        /// Control the server setup run style for the backtest: Automatic, Parallel or Series. 
        /// </summary>
        /// <remark>
        ///     Series mode runs all days through one computer, allowing memory of the previous days. 
        ///     Parallel mode runs all days separately which maximises speed but gives no memory of a previous day trading.
        /// </remark>
        /// <obsolete>The RunMode enum propert is now obsolete. All algorithms will default to RunMode.Series for series backtests.</obsolete>
        [Obsolete("The RunMode enum propert is now obsolete. All algorithms will default to RunMode.Series for series backtests.")]
        public RunMode RunMode 
        {
            get 
            {
                return _runMode;
            }
        }

        /// <summary>
        /// Boolean property indicating the algorithm is currently running in live mode. 
        /// </summary>
        /// <remarks>Intended for use where certain behaviors will be enabled while the algorithm is trading live: such as notification emails, or displaying runtime statistics.</remarks>
        public bool LiveMode
        {
            get
            {
                return _liveMode;
            }
        }

        /// <summary>
        /// Gets the current universe selector, or null if no selection is to be performed
        /// </summary>
        public IUniverse Universe
        {
            get; private set;
        }

        /// <summary>
        /// Gets the subscription settings to be used when adding securities via universe selection
        /// </summary>
        public SubscriptionSettings UniverseSettings
        {
            get; private set;
        }

        /// <summary>
        /// Storage for debugging messages before the event handler has passed control back to the Lean Engine.
        /// </summary>
        /// <seealso cref="Debug(string)"/>
        public List<string> DebugMessages
        {
            get 
            {
                return _debugMessages;
            }
            set 
            {
                _debugMessages = value;
            }
        }

        /// <summary>
        /// Storage for log messages before the event handlers have passed control back to the Lean Engine.
        /// </summary>
        /// <seealso cref="Log(string)"/>
        public List<string> LogMessages 
        {
            get 
            {
                return _logMessages;
            }
            set 
            {
                _logMessages = value;
            }
        }

        /// <summary>
        /// Gets the run time error from the algorithm, or null if none was encountered.
        /// </summary>
        public Exception RunTimeError { get; set; }

        /// <summary>
        /// List of error messages generated by the user's code calling the "Error" function.
        /// </summary>
        /// <remarks>This method is best used within a try-catch bracket to handle any runtime errors from a user algorithm.</remarks>
        /// <see cref="Error(string)"/>
        public List<string> ErrorMessages
        {
            get
            {
                return _errorMessages;
            }
            set
            {
                _errorMessages = value;
            }
        }

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        /// <seealso cref="SetStartDate(DateTime)"/>
        /// <seealso cref="SetEndDate(DateTime)"/>
        /// <seealso cref="SetCash(decimal)"/>
        public virtual void Initialize() 
        {
            //Setup Required Data
            throw new NotImplementedException("Please override the Intitialize() method");
        }

        /// <summary>
        /// Called by setup handlers after Initialize and allows the algorithm a chance to organize
        /// the data gather in the Initialize method
        /// </summary>
        public void PostInitialize()
        {
            // if the benchmark hasn't been set yet, set it
            if (Benchmark == null)
            {
                // apply the default benchmark if it hasn't been set
                if (_benchmarkSymbol == Symbol.Empty)
                {
                    _benchmarkSymbol = new Symbol("SPY");
                    _benchmarkSecurityType = SecurityType.Equity;
                }

                // if the requested benchmark system wasn't already added, then add it now
                Security security;
                if (!Securities.TryGetValue(_benchmarkSymbol, out security))
                {
                    // add the security as an internal feed so the algorithm doesn't receive the data
                    var resolution = _liveMode ? Resolution.Second : Resolution.Daily;
                    var market = _benchmarkSecurityType == SecurityType.Forex ? "fxcm" : "usa";
                    security = SecurityManager.CreateSecurity(Portfolio, SubscriptionManager, _exchangeHoursProvider, _benchmarkSecurityType, _benchmarkSymbol, resolution, market, true, 1m, false, true, false);
                }

                // just return the current price
                Benchmark = dateTime => security.Price;
            }
        }

        /// <summary>
        /// Event - v3.0 DATA EVENT HANDLER: (Pattern) Basic template for user to override for receiving all subscription data in a single event
        /// </summary>
        /// <code>
        /// TradeBars bars = slice.Bars;
        /// Ticks ticks = slice.Ticks;
        /// TradeBar spy = slice["SPY"];
        /// List{Tick} aaplTicks = slice["AAPL"]
        /// Quandl oil = slice["OIL"]
        /// dynamic anySymbol = slice[symbol];
        /// DataDictionary{Quandl} allQuandlData = slice.Get{Quand}
        /// Quandl oil = slice.Get{Quandl}("OIL")
        /// </code>
        /// <param name="slice">The current slice of data keyed by symbol string</param>
        public virtual void OnData(Slice slice)
        {
            // as a default implementation, let's look for and call OnData(Slice) just in case a user forgot to use the override keyword
            if (!_checkedForOnDataSlice)
            {
                _checkedForOnDataSlice = true;
                
                var method = GetType().GetMethods()
                    .Where(x => x.Name == "OnData")
                    .Where(x => x.DeclaringType != typeof(QCAlgorithm))
                    .Where(x => x.GetParameters().Length == 1)
                    .FirstOrDefault(x => x.GetParameters()[0].ParameterType == typeof (Slice));

                if (method == null)
                {
                    return;
                }

                var self = Expression.Constant(this);
                var parameter = Expression.Parameter(typeof (Slice), "data");
                var call = Expression.Call(self, method, parameter);
                var lambda = Expression.Lambda<Action<Slice>>(call, parameter);
                _onDataSlice = lambda.Compile();
            }
            // if we have it, then invoke it
            if (_onDataSlice != null)
            {
                _onDataSlice(slice);
            }
        }

        /// <summary>
        /// Event fired each time the we add/remove securities from the data feed
        /// </summary>
        /// <param name="changes"></param>
        public virtual void OnSecuritiesChanged(SecurityChanges changes)
        {
            
        }

        // <summary>
        // Event - v2.0 TRADEBAR EVENT HANDLER: (Pattern) Basic template for user to override when requesting tradebar data.
        // </summary>
        // <param name="data"></param>
        //public void OnData(TradeBars data)
        //{
        //
        //}

        // <summary>
        // Event - v2.0 TICK EVENT HANDLER: (Pattern) Basic template for user to override when requesting tick data.
        // </summary>
        // <param name="data">List of Tick Data</param>
        //public void OnData(Ticks data)
        //{
        //
        //}

        // <summary>
        // Event - v2.0 SPLIT EVENT HANDLER: (Pattern) Basic template for user to override when inspecting split data.
        // </summary>
        // <param name="data">IDictionary of Split Data Keyed by Symbol String</param>
        //public void OnData(Splits data)
        //{
        //
        //}

        // <summary>
        // Event - v2.0 DIVIDEND EVENT HANDLER: (Pattern) Basic template for user to override when inspecting dividend data
        // </summary>
        // <param name="data">IDictionary of Dividend Data Keyed by Symbol String</param>
        //public void OnData(Dividends data)
        //{
        //
        //}

        // <summary>
        // Event - v2.0 DELISTING EVENT HANDLER: (Pattern) Basic template for user to override when inspecting delisting data
        // </summary>
        // <param name="data">IDictionary of Delisting Data Keyed by Symbol String</param>
        //public void OnData(Delistings data)

        // <summary>
        // Event - v2.0 SYMBOL CHANGED EVENT HANDLER: (Pattern) Basic template for user to override when inspecting symbol changed data
        // </summary>
        // <param name="data">IDictionary of SymbolChangedEvent Data Keyed by Symbol String</param>
        //public void OnData(SymbolChangedEvents data)

        /// <summary>
        /// Margin call event handler. This method is called right before the margin call orders are placed in the market.
        /// </summary>
        /// <param name="requests">The orders to be executed to bring this algorithm within margin limits</param>
        public virtual void OnMarginCall(List<SubmitOrderRequest> requests)
        {
        }

        /// <summary>
        /// Margin call warning event handler. This method is called when Portoflio.MarginRemaining is under 5% of your Portfolio.TotalPortfolioValue
        /// </summary>
        public virtual void OnMarginCallWarning()
        {
        }

        /// <summary>
        /// End of a trading day event handler. This method is called at the end of the algorithm day (or multiple times if trading multiple assets).
        /// </summary>
        /// <remarks>Method is called 10 minutes before closing to allow user to close out position.</remarks>
        public virtual void OnEndOfDay()
        {

        }

        /// <summary>
        /// End of a trading day event handler. This method is called at the end of the algorithm day (or multiple times if trading multiple assets).
        /// </summary>
        /// <param name="symbol">Asset symbol for this end of day event. Forex and equities have different closing hours.</param>
        public virtual void OnEndOfDay(string symbol)
        {
        }

        /// <summary>
        /// End of a trading day event handler. This method is called at the end of the algorithm day (or multiple times if trading multiple assets).
        /// </summary>
        /// <param name="symbol">Asset symbol for this end of day event. Forex and equities have different closing hours.</param>
        public virtual void OnEndOfDay(Symbol symbol)
        {
            OnEndOfDay(symbol.SID);
        }

        /// <summary>
        /// End of algorithm run event handler. This method is called at the end of a backtest or live trading operation. Intended for closing out logs.
        /// </summary>
        public virtual void OnEndOfAlgorithm() 
        { 
            
        }

        /// <summary>
        /// Order fill event handler. On an order fill update the resulting information is passed to this method.
        /// </summary>
        /// <param name="orderEvent">Order event details containing details of the evemts</param>
        /// <remarks>This method can be called asynchronously and so should only be used by seasoned C# experts. Ensure you use proper locks on thread-unsafe objects</remarks>
        public virtual void OnOrderEvent(OrderEvent orderEvent)
        {
   
        }

        /// <summary>
        /// Update the internal algorithm time frontier.
        /// </summary>
        /// <remarks>For internal use only to advance time.</remarks>
        /// <param name="frontier">Current datetime.</param>
        public void SetDateTime(DateTime frontier) 
        {
            _timeKeeper.SetUtcDateTime(frontier);
        }

        /// <summary>
        /// Sets the time zone of the <see cref="Time"/> property in the algorithm
        /// </summary>
        /// <param name="timeZone">The desired time zone</param>
        public void SetTimeZone(string timeZone)
        {
            DateTimeZone tz;
            try
            {
                tz = DateTimeZoneProviders.Tzdb[timeZone];
            }
            catch (DateTimeZoneNotFoundException)
            {
                throw new ArgumentException(string.Format("TimeZone with id '{0}' was not found. For a complete list of time zones please visit: http://en.wikipedia.org/wiki/List_of_tz_database_time_zones", timeZone));
            }

            SetTimeZone(tz);
        }

        /// <summary>
        /// Sets the time zone of the <see cref="Time"/> property in the algorithm
        /// </summary>
        /// <param name="timeZone">The desired time zone</param>
        public void SetTimeZone(DateTimeZone timeZone)
        {
            if (_locked)
            {
                throw new Exception("Algorithm.SetTimeZone(): Cannot change time zone after algorithm running.");
            }

            if (timeZone == null) throw new ArgumentNullException("timeZone");
            _timeKeeper.AddTimeZone(timeZone);
            _localTimeKeeper = _timeKeeper.GetLocalTimeKeeper(timeZone);

            // the time rules need to know the default time zone as well
            TimeRules.SetDefaultTimeZone(timeZone);
        }

        /// <summary>
        /// Set the RunMode for the Servers. If you are running an overnight algorithm, you must select series.
        /// Automatic will analyse the selected data, and if you selected only minute data we'll select series for you.
        /// </summary>
        /// <obsolete>This method is now obsolete and has no replacement. All algorithms now run in Series mode.</obsolete>
        /// <param name="mode">Enum RunMode with options Series, Parallel or Automatic. Automatic scans your requested symbols and resolutions and makes a decision on the fastest analysis</param>
        [Obsolete("This method is now obsolete and has no replacement. All algorithms now run in Series mode.")]
        public void SetRunMode(RunMode mode) 
        {
            if (mode != RunMode.Parallel) return;
            Debug("Algorithm.SetRunMode(): RunMode-Parallel Type has been deprecated. Series analysis selected instead");
        }

        /// <summary>
        /// Sets the brokerage to emulate in backtesting or paper trading.
        /// This can be used for brokerages that have been implemented in LEAN
        /// </summary>
        /// <param name="brokerage">The brokerage to emulate</param>
        public void SetBrokerageModel(BrokerageName brokerage)
        {
            switch (brokerage)
            {
                case BrokerageName.Default:
                    BrokerageModel = new DefaultBrokerageModel();
                    break;
                case BrokerageName.InteractiveBrokersBrokerage:
                    BrokerageModel = new InteractiveBrokersBrokerageModel();
                    break;

                case BrokerageName.TradierBrokerage:
                    BrokerageModel = new TradierBrokerageModel();
                    break;

                default:
                    throw new ArgumentOutOfRangeException("brokerage", brokerage, null);
            }
        }

        /// <summary>
        /// Sets the benchmark used for computing statistics of the algorithm to the specified symbol
        /// </summary>
        /// <param name="symbol">symbol to use as the benchmark</param>
        /// <param name="securityType">Is the symbol an equity, option, forex, etc. Default SecurityType.Equity</param>
        /// <remarks>
        /// Must use symbol that is available to the trade engine in your data store(not strictly enforced)
        /// </remarks>
        public void SetBenchmark(SecurityType securityType, Symbol symbol)
        {
            _benchmarkSymbol = symbol;
            _benchmarkSecurityType = securityType;
        }

        /// <summary>
        /// Sets the benchmark used for computing statistics of the algorithm to the specified symbol, defaulting to SecurityType.Equity
        /// if the symbol doesn't exist in the algorithm
        /// </summary>
        /// <param name="symbol">symbol to use as the benchmark</param>
        /// <remarks>
        /// Overload to accept symbol without passing SecurityType. If symbol is in portfolio it will use that SecurityType, otherwise will default to SecurityType.Equity
        /// </remarks>
        public void SetBenchmark(Symbol symbol)
        {
            _benchmarkSymbol = symbol;
            _benchmarkSecurityType = SecurityType.Equity;
        }

        /// <summary>
        /// Sets the specified function as the benchmark, this function provides the value of
        /// the benchmark at each date/time requested
        /// </summary>
        /// <param name="benchmark">The benchmark producing function</param>
        public void SetBenchmark(Func<DateTime, decimal> benchmark)
        {
            Benchmark = benchmark;
        }

        /// <summary>
        /// Benchmark
        /// </summary>
        /// <remarks>Use Benchmark to override default symbol based benchmark, and create your own benchmark. For example a custom moving average benchmark </remarks>
        /// 
        public Func<DateTime, decimal> Benchmark
        {
            get;
            private set;
        }

   
        /// <summary>
        /// Set initial cash for the strategy while backtesting. During live mode this value is ignored 
        /// and replaced with the actual cash of your brokerage account.
        /// </summary>
        /// <param name="startingCash">Starting cash for the strategy backtest</param>
        /// <remarks>Alias of SetCash(decimal)</remarks>
        public void SetCash(double startingCash)
        {
            SetCash((decimal)startingCash);
        }

        /// <summary>
        /// Set initial cash for the strategy while backtesting. During live mode this value is ignored 
        /// and replaced with the actual cash of your brokerage account.
        /// </summary>
        /// <param name="startingCash">Starting cash for the strategy backtest</param>
        /// <remarks>Alias of SetCash(decimal)</remarks>
        public void SetCash(int startingCash)
        {
            SetCash((decimal)startingCash);
        }

        /// <summary>
        /// Set initial cash for the strategy while backtesting. During live mode this value is ignored 
        /// and replaced with the actual cash of your brokerage account.
        /// </summary>
        /// <param name="startingCash">Starting cash for the strategy backtest</param>
        public void SetCash(decimal startingCash)
        {
            if (!_locked)
            {
                Portfolio.SetCash(startingCash);
            }
            else
            {
                throw new Exception("Algorithm.SetCash(): Cannot change cash available after algorithm initialized.");
            }
        }

        /// <summary>
        /// Set the cash for the specified symbol
        /// </summary>
        /// <param name="symbol">The cash symbol to set</param>
        /// <param name="startingCash">Decimal cash value of portfolio</param>
        /// <param name="conversionRate">The current conversion rate for the</param>
        public void SetCash(string symbol, decimal startingCash, decimal conversionRate)
        {
            if (!_locked)
            {
                Portfolio.SetCash(symbol, startingCash, conversionRate);
            }
            else
            {
                throw new Exception("Algorithm.SetCash(): Cannot change cash available after algorithm initialized.");
            }
        }

        /// <summary>
        /// Set the start date for backtest.
        /// </summary>
        /// <param name="day">Int starting date 1-30</param>
        /// <param name="month">Int month starting date</param>
        /// <param name="year">Int year starting date</param>
        /// <remarks> 
        ///     Wrapper for SetStartDate(DateTime). 
        ///     Must be less than end date. 
        ///     Ignored in live trading mode.
        /// </remarks>
        public void SetStartDate(int year, int month, int day) 
        {
            try
            {
                var start = new DateTime(year, month, day);

                // We really just want the date of the start, so it's 12am of the requested day (first moment of the day)
                start = start.Date;

                SetStartDate(start);
            }
            catch (Exception err) 
            {
                throw new Exception("Date Invalid: " + err.Message);
            }
        }

        /// <summary>
        /// Set the end date for a backtest run 
        /// </summary>
        /// <param name="day">Int end date 1-30</param>
        /// <param name="month">Int month end date</param>
        /// <param name="year">Int year end date</param>
        /// <remarks>Wrapper for SetEndDate(datetime).</remarks>
        /// <seealso cref="SetEndDate(DateTime)"/>
        public void SetEndDate(int year, int month, int day) 
        {
            try
            {
                var end = new DateTime(year, month, day);

                // we want the end date to be just before the next day (last moment of the day)
                end = end.Date.AddDays(1).Subtract(TimeSpan.FromTicks(1));

                SetEndDate(end);
            }
            catch (Exception err) 
            {
                throw new Exception("Date Invalid: " + err.Message);
            }
        }

        /// <summary>
        /// Set the algorithm id (backtestId or live deployId for the algorithmm).
        /// </summary>
        /// <param name="algorithmId">String Algorithm Id</param>
        /// <remarks>Intended for internal QC Lean Engine use only as a setter for AlgorihthmId</remarks>
        public void SetAlgorithmId(string algorithmId)
        {
            _algorithmId = algorithmId;
        }

        /// <summary>
        /// Set the start date for the backtest 
        /// </summary>
        /// <param name="start">Datetime Start date for backtest</param>
        /// <remarks>Must be less than end date and within data available</remarks>
        /// <seealso cref="SetStartDate(DateTime)"/>
        public void SetStartDate(DateTime start) 
        { 
            //Validate the start date:
            //1. Check range;
            if (start < (new DateTime(1900, 01, 01)))
            {
                throw new Exception("Please select a start date after January 1st, 1900.");
            }

            //2. Check end date greater:
            if (_endDate != new DateTime()) 
            {
                if (start > _endDate) 
                {
                    throw new Exception("Please select start date less than end date.");
                }
            }

            //3. Round up and subtract one tick:
            start = start.RoundDown(TimeSpan.FromDays(1));

            //3. Check not locked already:
            if (!_locked) 
            {
                // this is only or backtesting
                if (!LiveMode)
                {
                    _startDate = start;
                    SetDateTime(_startDate.ConvertToUtc(TimeZone));
                }
            } 
            else
            {
                throw new Exception("Algorithm.SetStartDate(): Cannot change start date after algorithm initialized.");
            }
        }

        /// <summary>
        /// Set the end date for a backtest.
        /// </summary>
        /// <param name="end">Datetime value for end date</param>
        /// <remarks>Must be greater than the start date</remarks>
        /// <seealso cref="SetEndDate(DateTime)"/>
        public void SetEndDate(DateTime end) 
        { 
            //Validate:
            //1. Check Range:
            if (end > DateTime.Now.Date.AddDays(-1)) 
            {
                end = DateTime.Now.Date.AddDays(-1);
            }

            //2. Check start date less:
            if (_startDate != new DateTime()) 
            {
                if (end < _startDate) 
                {
                    throw new Exception("Please select end date greater than start date.");
                }
            }

            //3. Make this at the very end of the requested date
            end = end.RoundDown(TimeSpan.FromDays(1)).AddDays(1).AddTicks(-1);

            //4. Check not locked already:
            if (!_locked) 
            {
                _endDate = end;
            }
            else 
            {
                throw new Exception("Algorithm.SetEndDate(): Cannot change end date after algorithm initialized.");
            }
        }

        /// <summary>
        /// Lock the algorithm initialization to avoid user modifiying cash and data stream subscriptions
        /// </summary>
        /// <remarks>Intended for Internal QC Lean Engine use only to prevent accidental manipulation of important properties</remarks>
        public void SetLocked()
        {
            _locked = true;
        }

        /// <summary>
        /// Gets whether or not this algorithm has been locked and fully initialized
        /// </summary>
        public bool GetLocked()
        {
            return _locked;
        }

        /// <summary>
        /// Set live mode state of the algorithm run: Public setter for the algorithm property LiveMode.
        /// </summary>
        public void SetLiveMode(bool live) 
        {
            if (!_locked)
            {
                _liveMode = live;
                Notify = new NotificationManager(live);
                TradeBuilder.SetLiveMode(live);
            }
        }

        /// <summary>
        /// Sets the current universe selector for the algorithm. This will be executed on day changes
        /// </summary>
        /// <param name="selector">The universe selector</param>
        public void SetUniverse(IUniverse selector)
        {
            Universe = selector;
        }

        /// <summary>
        /// Sets the current universe selector for the algorithm. This will be executed on day changes
        /// </summary>
        /// <param name="coarse">Defines an initial coarse selection</param>
        public void SetUniverse(Func<IEnumerable<CoarseFundamental>, IEnumerable<CoarseFundamental>> coarse)
        {
            Universe = new FuncUniverse(coarse);
        }

        /// <summary>
        /// Get the history for all configured securities over the requested span.
        /// This will use the resolution and other subscription settings for each security.
        /// The symbols must exist in the Securities collection.
        /// </summary>
        /// <param name="span">The span over which to request data. This is a calendar span, so take into consideration weekends and such</param>
        /// <returns>An enumerable of slice containing data over the most recent span for all configured securities</returns>
        public IEnumerable<Slice> History(TimeSpan span, Resolution? resolution = null)
        {
            return History(Securities.Keys, Time - span, Time, resolution);
        }

        public IEnumerable<TradeBar> History(Symbol symbol, int periods, Resolution? resolution = null)
        {
            var security = Securities[symbol];
            var start = GetStartTimeAlgoTz(symbol, periods, resolution);
            return History(new[] {symbol}, start, Time.RoundDown((resolution ?? security.Resolution).ToTimeSpan()), resolution).Get(symbol);
        }

        /// <summary>
        /// Gets the historical data for all symbols of the requested type over the requested span.
        /// The symbol's configured values for resolution and fill forward behavior will be used
        /// The symbols must exist in the Securities collection.
        /// </summary>
        /// <param name="span">The span over which to retrieve recent historical data</param>
        /// <returns>An enumerable of slice containing the requested historical data</returns>
        public IEnumerable<DataDictionary<T>> History<T>(TimeSpan span, Resolution? resolution = null)
            where T : BaseData
        {
            return History<T>(Securities.Keys, span, resolution);
        }

        /// <summary>
        /// Gets the historical data for the specified symbols over the requested span.
        /// The symbols must exist in the Securities collection.
        /// </summary>
        /// <typeparam name="T">The data type of the symbols</typeparam>
        /// <param name="symbols">The symbols to retrieve historical data for</param>
        /// <param name="span">The span over which to retrieve recent historical data</param>
        /// <param name="resolution">The resolution to request</param>
        /// <returns>An enumerable of slice containing the requested historical data</returns>
        public IEnumerable<DataDictionary<T>> History<T>(IEnumerable<Symbol> symbols, TimeSpan span, Resolution? resolution = null)
            where T : BaseData
        {
            return History<T>(symbols, Time - span, Time, resolution);
        }

        /// <summary>
        /// Gets the historical data for the specified symbols. The exact number of bars will be returned for
        /// each symbol. This may result in some data start earlier/later than others due to when various
        /// exchanges are open. The symbols must exist in the Securities collection.
        /// </summary>
        /// <typeparam name="T">The data type of the symbols</typeparam>
        /// <param name="symbols">The symbols to retrieve historical data for</param>
        /// <param name="periods">The number of bars to request</param>
        /// <param name="resolution">The resolution to request</param>
        /// <returns>An enumerable of slice containing the requested historical data</returns>
        public IEnumerable<DataDictionary<T>> History<T>(IEnumerable<Symbol> symbols, int periods, Resolution? resolution = null) 
            where T : BaseData
        {
            var requests = symbols.Select(x =>
            {
                var security = Securities[x];
                // don't make requests for symbols of the wrong type
                if (!typeof(T).IsAssignableFrom(security.SubscriptionDataConfig.Type)) return null;
                var start = GetStartTimeAlgoTz(x, periods, resolution).ConvertToUtc(TimeZone);
                return new HistoryRequest(security, start, UtcTime.RoundDown((resolution ?? security.Resolution).ToTimeSpan()))
                {
                    Resolution = resolution ?? security.Resolution,
                    FillForwardResolution = security.IsFillDataForward ? resolution : null
                };
            });

            return History(requests.Where(x => x != null)).Get<T>();
        }

        /// <summary>
        /// Gets the historical data for the specified symbols between the specified dates. The symbols must exist in the Securities collection.
        /// </summary>
        /// <typeparam name="T">The data type of the symbols</typeparam>
        /// <param name="symbols">The symbols to retrieve historical data for</param>
        /// <param name="start">The start time in the algorithm's time zone</param>
        /// <param name="end">The end time in the algorithm's time zone</param>
        /// <param name="resolution">The resolution to request</param>
        /// <returns>An enumerable of slice containing the requested historical data</returns>
        public IEnumerable<DataDictionary<T>> History<T>(IEnumerable<Symbol> symbols, DateTime start, DateTime end, Resolution? resolution = null) 
            where T : BaseData
        {
            var requests = symbols.Select(x =>
            {
                var security = Securities[x];
                // don't make requests for symbols of the wrong type
                if (!typeof (T).IsAssignableFrom(security.SubscriptionDataConfig.Type)) return null;
                return new HistoryRequest(security, start.ConvertToUtc(TimeZone), end.ConvertToUtc(TimeZone))
                {
                    Resolution = resolution ?? security.Resolution,
                    FillForwardResolution = security.IsFillDataForward ? resolution : (Resolution?)null
                };
            });

            return History(requests.Where(x => x != null)).Get<T>();
        }

        /// <summary>
        /// Gets the historical data for the specified symbol over the request span. The symbol must exist in the Securities collection.
        /// </summary>
        /// <typeparam name="T">The data type of the symbol</typeparam>
        /// <param name="symbol">The symbol to retrieve historical data for</param>
        /// <param name="span">The span over which to retrieve recent historical data</param>
        /// <param name="resolution">The resolution to request</param>
        /// <returns>An enumerable of slice containing the requested historical data</returns>
        public IEnumerable<T> History<T>(Symbol symbol, TimeSpan span, Resolution? resolution = null)
            where T : BaseData
        {
            return History<T>(symbol, Time - span, Time, resolution);
        }

        /// <summary>
        /// Gets the historical data for the specified symbol. The exact number of bars will be returned. 
        /// The symbols must exist in the Securities collection.
        /// </summary>
        /// <typeparam name="T">The data type of the symbol</typeparam>
        /// <param name="symbol">The symbol to retrieve historical data for</param>
        /// <param name="periods">The number of bars to request</param>
        /// <param name="resolution">The resolution to request</param>
        /// <returns>An enumerable of slice containing the requested historical data</returns>
        public IEnumerable<T> History<T>(Symbol symbol, int periods, Resolution? resolution = null)
            where T : BaseData
        {
            if (resolution == Resolution.Tick) throw new ArgumentException("History functions that accept a 'periods' parameter can not be used with Resolution.Tick");
            var security = Securities[symbol];
            // verify the types match
            var actualType = security.SubscriptionDataConfig.Type;
            var requestedType = typeof(T);
            if (!requestedType.IsAssignableFrom(actualType))
            {
                throw new ArgumentException("The specified security is not of the requested type. Symbol: " + symbol + " Requested Type: " + requestedType.Name + " Actual Type: " + actualType);
            }

            var start = GetStartTimeAlgoTz(symbol, periods, resolution);
            return History<T>(symbol, start, Time.RoundDown((resolution ?? security.Resolution).ToTimeSpan()), resolution);
        }

        /// <summary>
        /// Gets the historical data for the specified symbol between the specified dates. The symbol must exist in the Securities collection.
        /// </summary>
        /// <param name="symbol">The symbol to retrieve historical data for</param>
        /// <param name="start">The start time in the algorithm's time zone</param>
        /// <param name="end">The end time in the algorithm's time zone</param>
        /// <param name="resolution">The resolution to request</param>
        /// <returns>An enumerable of slice containing the requested historical data</returns>
        public IEnumerable<T> History<T>(Symbol symbol, DateTime start, DateTime end, Resolution? resolution = null)
            where T : BaseData
        {
            var security = Securities[symbol];

            // verify the types match
            var actualType = security.SubscriptionDataConfig.Type;
            var requestedType = typeof(T);
            if (!requestedType.IsAssignableFrom(actualType))
            {
                throw new ArgumentException("The specified security is not of the requested type. Symbol: " + symbol + " Requested Type: " + requestedType.Name + " Actual Type: " + actualType);
            }

            var fillForwardResolution = security.IsFillDataForward ? resolution : null;
            var request = new HistoryRequest(security, start.ConvertToUtc(TimeZone), end.ConvertToUtc(TimeZone))
            {
                Resolution = resolution ?? security.Resolution,
                FillForwardResolution = fillForwardResolution
            };
            return History(request).Get<T>(symbol);
        }

        /// <summary>
        /// Gets the historical data for the specified symbol over the request span. The symbol must exist in the Securities collection.
        /// </summary>
        /// <param name="symbol">The symbol to retrieve historical data for</param>
        /// <param name="span">The span over which to retrieve recent historical data</param>
        /// <param name="resolution">The resolution to request</param>
        /// <returns>An enumerable of slice containing the requested historical data</returns>
        public IEnumerable<TradeBar> History(Symbol symbol, TimeSpan span, Resolution? resolution = null)
        {
            return History(new[] {symbol}, span, resolution).Get(symbol);
        }

        /// <summary>
        /// Gets the historical data for the specified symbols over the requested span.
        /// The symbol's configured values for resolution and fill forward behavior will be used
        /// The symbols must exist in the Securities collection.
        /// </summary>
        /// <param name="symbols">The symbols to retrieve historical data for</param>
        /// <param name="span">The span over which to retrieve recent historical data</param>
        /// <param name="resolution">The resolution to request</param>
        /// <returns>An enumerable of slice containing the requested historical data</returns>
        public IEnumerable<Slice> History(IEnumerable<Symbol> symbols, TimeSpan span, Resolution? resolution = null)
        {
            return History(symbols, Time - span, Time, resolution);
        }

        /// <summary>
        /// Gets the historical data for the specified symbols. The exact number of bars will be returned for
        /// each symbol. This may result in some data start earlier/later than others due to when various
        /// exchanges are open. The symbols must exist in the Securities collection.
        /// </summary>
        /// <param name="symbols">The symbols to retrieve historical data for</param>
        /// <param name="periods">The number of bars to request</param>
        /// <param name="resolution">The resolution to request</param>
        /// <returns>An enumerable of slice containing the requested historical data</returns>
        public IEnumerable<Slice> History(IEnumerable<Symbol> symbols, int periods, Resolution? resolution = null)
        {
            if (resolution == Resolution.Tick) throw new ArgumentException("History functions that accept a 'periods' parameter can not be used with Resolution.Tick");
            return History(symbols.Select(x =>
            {
                var security = Securities[x];
                var start = GetStartTimeAlgoTz(x, periods, resolution).ConvertToUtc(security.Exchange.TimeZone);
                return new HistoryRequest(security, start, UtcTime.RoundDown((resolution ?? security.Resolution).ToTimeSpan()))
                {
                    Resolution = resolution ?? security.Resolution,
                    FillForwardResolution = security.IsFillDataForward ? resolution : (Resolution?) null
                };
            }));
        }

        /// <summary>
        /// Gets the historical data for the specified symbols between the specified dates. The symbols must exist in the Securities collection.
        /// </summary>
        /// <param name="symbols">The symbols to retrieve historical data for</param>
        /// <param name="start">The start time in the algorithm's time zone</param>
        /// <param name="end">The end time in the algorithm's time zone</param>
        /// <param name="resolution">The resolution to request</param>
        /// <param name="fillForward">True to fill forward missing data, false otherwise</param>
        /// <param name="extendedMarket">True to include extended market hours data, false otherwise</param>
        /// <returns>An enumerable of slice containing the requested historical data</returns>
        public IEnumerable<Slice> History(IEnumerable<Symbol> symbols, DateTime start, DateTime end, Resolution? resolution = null, bool? fillForward = null, bool? extendedMarket = null)
        {
            return History(symbols.Select(x =>
            {
                var security = Securities[x];
                resolution = resolution ?? security.Resolution;
                var request = new HistoryRequest(security, start.ConvertToUtc(TimeZone), end.ConvertToUtc(TimeZone))
                {
                    Resolution = resolution.Value,
                    FillForwardResolution = security.IsFillDataForward ? resolution : null
                };
                // apply overrides
                if (fillForward.HasValue) request.FillForwardResolution = fillForward.Value ? resolution : null;
                if (extendedMarket.HasValue) request.IncludeExtendedMarketHours = extendedMarket.Value;
                return request;
            }));
        }

        /// <summary>
        /// Gets the start time required for the specified bar count in terms of the algorithm's time zone
        /// </summary>
        private DateTime GetStartTimeAlgoTz(Symbol symbol, int periods, Resolution? resolution = null)
        {
            var security = Securities[symbol];
            var localStartTime = QuantConnect.Time.GetStartTimeForTradeBars(security.Exchange.Hours, UtcTime.ConvertFromUtc(security.Exchange.TimeZone), (resolution ?? security.Resolution).ToTimeSpan(), periods, security.IsExtendedMarketHours);
            return localStartTime.ConvertTo(security.Exchange.TimeZone, TimeZone);
        }

        ///// <summary>
        ///// Gets the history for the specified symbol over the requested span. This function
        ///// requires that the specified symbol has already been configured for receiving data and
        ///// will use the configuration for values such as fill forward and extended market hours.
        ///// </summary>
        ///// <param name="symbol">The symbol to request historical data for</param>
        ///// <param name="span">The span over which to request data. This is a calendar span, so take into consideration weekends and such</param>
        ///// <returns>An enumerable of slice containing data over the most recent span for the specified configured security</returns>
        //public IEnumerable<Slice> History(Symbol symbol, TimeSpan span)
        //{
        //    return History(new HistoryRequest(Securities[symbol], UtcTime - span, UtcTime));
        //}

        ///// <summary>
        ///// Gets the requested number of bars of history for the specified symbol. This function
        ///// requires that the specified symbol has already been configured for receiving data and
        ///// will use the configuration for values such as fill forward and extended market hours.
        ///// </summary>
        ///// <param name="symbol">The symbol to request historical data for</param>
        ///// <param name="periods">The number of trade bars to receive at the resolution.</param>
        ///// <param name="resolution">The requested data resolution. This must not equal <see cref="Resolution.Tick"/></param>
        ///// <returns>An enumerable of slice containing the specified number of bars at the specified resolution for the configured security</returns>
        //public IEnumerable<Slice> History(Symbol symbol, int periods, Resolution resolution)
        //{
        //    if (resolution == Resolution.Tick) throw new ArgumentException("History functions that accept a bar count require that the resolution does not equal Resolution.Tick");
        //    var security = Securities[symbol];
        //    var config = security.SubscriptionDataConfig;
        //    return History(symbol, periods, resolution, config.FillDataForward, config.ExtendedMarketHours);
        //}

        ///// <summary>
        ///// Gets the requested number of bars of history for the specified symbol. This function
        ///// requires that the specified symbol has already been configured for receiving data.
        ///// </summary>
        ///// <param name="symbol">The symbol to request historical data for</param>
        ///// <param name="periods">The number of trade bars to receive at the resolution.</param>
        ///// <param name="resolution">The requested data resolution. This must not equal <see cref="Resolution.Tick"/></param>
        ///// <param name="fillForward">True to enable fill forward behavior, false otherwise</param>
        ///// <param name="extendedMarket">True to receive pre and post market data, false for only normal market hours data</param>
        ///// <returns>An enumerable of slice containing the specified number of bars at the specified resolution for the configured security</returns>
        //public IEnumerable<Slice> History(Symbol symbol, int periods, Resolution resolution, bool fillForward, bool extendedMarket = false)
        //{
        //    if (resolution == Resolution.Tick) throw new ArgumentException("History functions that accept a bar count require that the resolution does not equal Resolution.Tick");
        //    var security = Securities[symbol];
        //    var start = QuantConnect.Time.GetStartTimeForTradeBars(security.Exchange.Hours, Time, resolution.ToTimeSpan(), periods, extendedMarket);
        //    return History(new[] {symbol}, start, Time, resolution, fillForward, extendedMarket);
        //}

        ///// <summary>
        ///// Gets the requested number of bars of history for the specified symbol. This function
        ///// does NOT require that the specified symbol has been configured for receiving data.
        ///// </summary>
        ///// <param name="symbol">The symbol to request historical data for</param>
        ///// <param name="securityType">The security type of the symbol. This  must not equal <see cref="SecurityType.Base"/> unless the symbol has been configured.</param>
        ///// <param name="periods">The number of trade bars to receive at the resolution.</param>
        ///// <param name="resolution">The requested data resolution. This must not equal <see cref="Resolution.Tick"/></param>
        ///// <param name="fillForward">True to enable fill forward behavior, false otherwise</param>
        ///// <param name="extendedMarket">True to receive pre and post market data, false for only normal market hours data</param>
        ///// <returns>An enumerable of slice containing the specified number of bars at the specified resolution for the specified symbol</returns>
        //public IEnumerable<Slice> History(Symbol symbol, SecurityType securityType, int periods, Resolution resolution, bool fillForward, bool extendedMarket = false)
        //{
        //    if (resolution == Resolution.Tick) throw new ArgumentException("History functions that accept a bar count require that the resolution does not equal Resolution.Tick");
        //    Security security;
        //    SecurityExchangeHours exchangeHours;
        //    if (Securities.TryGetValue(symbol, out security) && securityType == security.Type)
        //    {
        //        exchangeHours = security.Exchange.Hours;
        //    }
        //    else if (securityType == SecurityType.Base)   exchangeHours = SecurityExchangeHours.AlwaysOpen(TimeZone);
        //    else if (securityType == SecurityType.Equity) exchangeHours = _exchangeHoursProvider.GetExchangeHours("usa", symbol, SecurityType.Equity);
        //    else if (securityType == SecurityType.Forex)  exchangeHours = _exchangeHoursProvider.GetExchangeHours("fxcm", symbol, SecurityType.Forex);
        //    else throw new ArgumentException("The specified symbol/security type is not supported: " + symbol + " " + securityType);

        //    var start = QuantConnect.Time.GetStartTimeForTradeBars(exchangeHours, Time, resolution.ToTimeSpan(), periods, extendedMarket);
        //    return History(symbol, securityType, start, Time, resolution, fillForward, extendedMarket);
        //}

        ///// <summary>
        ///// Gets the requested number of bars of history for the specified symbol. This function
        ///// does NOT require that the specified symbol has been configured for receiving data.
        ///// </summary>
        ///// <param name="symbol">The symbol to request historical data for</param>
        ///// <param name="securityType">The security type of the symbol. This  must not equal <see cref="SecurityType.Base"/> unless the symbol has been configured.</param>
        ///// <param name="start">The start time of the historical data request</param>
        ///// <param name="end">The end time of the historical data request</param>
        ///// <param name="resolution">The requested data resolution. This must not equal <see cref="Resolution.Tick"/></param>
        ///// <param name="fillForward">True to enable fill forward behavior, false otherwise</param>
        ///// <param name="extendedMarket">True to receive pre and post market data, false for only normal market hours data</param>
        ///// <param name="market">The market this symbol belongs to. If not specified, will default to 'usa' for equities and 'fxcm' for forex</param>
        ///// <returns>An enumerable of slice containing the data over the requested period at the specified resolution for the specified symbol</returns>
        //public IEnumerable<Slice> History(Symbol symbol, SecurityType securityType, DateTime start, DateTime end, Resolution resolution, bool fillForward, bool extendedMarket = false, string market = null)
        //{
        //    if (market == null)
        //    {
        //        if (securityType == SecurityType.Equity) market = "usa";
        //        if (securityType == SecurityType.Forex)  market = "fxcm";
        //    }
            
        //    Security security;
        //    SecurityExchangeHours exchangeHours;
        //    if (Securities.TryGetValue(symbol, out security) && security.Type == securityType) exchangeHours = security.Exchange.Hours;
        //    else if (securityType == SecurityType.Base) throw new ArgumentException("Unable to request history for unregistered custom data. Please add using the AddData method.");
        //    else exchangeHours = _exchangeHoursProvider.GetExchangeHours(market, symbol, securityType);

        //    var dataType = resolution == Resolution.Tick ? typeof(Tick) : typeof(TradeBar);
        //    var fillForwardResolution = fillForward && resolution != Resolution.Tick ? resolution : (Resolution?)null;
        //    var request = new HistoryRequest(start.ConvertToUtc(TimeZone), end.ConvertToUtc(TimeZone), dataType, symbol, securityType, resolution, market, exchangeHours, fillForwardResolution, extendedMarket, false);
        //    return History(request);
        //}

        ///// <summary>
        ///// Gets the requested number of bars of history for the specified symbols. This function
        ///// requires that the specified symbols have already been configured for receiving data and
        ///// will use the configuration for values such as fill forward and extended market hours.
        ///// </summary>
        ///// <param name="symbols">The symbols to request historical data for</param>
        ///// <param name="periods">The number of trade bars to receive at the resolution for each symbol.</param>
        ///// <param name="resolution">The requested data resolution. This must not equal <see cref="Resolution.Tick"/></param>
        ///// <returns>An enumerable of slice containing the specified number of bars at the specified resolution for the configured securities</returns>
        //public IEnumerable<Slice> History(IEnumerable<Symbol> symbols, int periods, Resolution resolution)
        //{
        //    if (resolution == Resolution.Tick) throw new ArgumentException("History functions that accept a bar count require that the resolution does not equal Resolution.Tick");
        //    return History(symbols, periods, resolution, true);
        //}

        ///// <summary>
        ///// Gets the requested number of bars of history for the specified symbols. This function
        ///// requires that the specified symbols have already been configured for receiving data.
        ///// </summary>
        ///// <param name="symbols">The symbols to request historical data for</param>
        ///// <param name="periods">The number of trade bars to receive at the resolution for each symbol.</param>
        ///// <param name="resolution">The requested data resolution. This must not equal <see cref="Resolution.Tick"/></param>
        ///// <param name="fillForward">True to enable fill forward behavior, false otherwise</param>
        ///// <param name="extendedMarket">True to receive pre and post market data, false for only normal market hours data</param>
        ///// <returns>An enumerable of slice containing the specified number of bars at the specified resolution for the configured securities</returns>
        //public IEnumerable<Slice> History(IEnumerable<Symbol> symbols, int periods, Resolution resolution, bool fillForward, bool extendedMarket = false)
        //{
        //    if (resolution == Resolution.Tick) throw new ArgumentException("History functions that accept a bar count require that the resolution does not equal Resolution.Tick");
        //    var requests = symbols.Select(symbol =>
        //    {
        //        var security = Securities[symbol];
        //        var config = security.SubscriptionDataConfig;
        //        var securityTimeZone = config.TimeZone;
        //        var start = QuantConnect.Time.GetStartTimeForTradeBars(security.Exchange.Hours, security.LocalTime, resolution.ToTimeSpan(), periods, extendedMarket).ConvertToUtc(securityTimeZone);
        //        var end = security.LocalTime.ConvertToUtc(securityTimeZone);
        //        return new HistoryRequest(start, end, config.Type, symbol, security.Type, resolution, config.Market, security.Exchange.Hours, fillForward ? resolution : (Resolution?) null, extendedMarket, config.IsCustomData);
        //    });
        //    return History(requests, TimeZone);
        //}

        ///// <summary>
        ///// Gets the requested number of bars of history for the specified symbols. This function
        ///// requires that the specified symbols have already been configured for receiving data.
        ///// </summary>
        ///// <param name="symbols">The symbols to request historical data for</param>
        ///// <param name="start">The start time of the historical data request</param>
        ///// <param name="end">The end time of the historical data request</param>
        ///// <param name="resolution">The requested data resolution. This must not equal <see cref="Resolution.Tick"/></param>
        ///// <param name="fillForward">True to enable fill forward behavior, false otherwise</param>
        ///// <param name="extendedMarket">True to receive pre and post market data, false for only normal market hours data</param>
        ///// <returns>An enumerable of slice containing the specified number of bars at the specified resolution for the configured securities</returns>
        //public IEnumerable<Slice> History(IEnumerable<Symbol> symbols, DateTime start, DateTime end, Resolution resolution, bool fillForward, bool extendedMarket = false)
        //{
        //    start = start.ConvertToUtc(TimeZone);
        //    end = end.ConvertToUtc(TimeZone);
        //    var requests = symbols.Select(symbol =>
        //    {
        //        // this overload requires that the symbols exist
        //        var security = Securities[symbol];
        //        var config = security.SubscriptionDataConfig;
        //        return new HistoryRequest(start,
        //            end,
        //            config.Type,
        //            symbol,
        //            security.Type,
        //            resolution,
        //            config.Market,
        //            security.Exchange.Hours,
        //            fillForward ? resolution : (Resolution?) null,
        //            extendedMarket,
        //            config.IsCustomData
        //            );
        //    });
        //    return History(requests, TimeZone);
        //}

        /// <summary>
        /// Executes the specified history request
        /// </summary>
        /// <param name="request">the history request to execute</param>
        /// <returns>An enumerable of slice satisfying the specified history request</returns>
        public IEnumerable<Slice> History(HistoryRequest request)
        {
            return History(new[] {request});
        }

        /// <summary>
        /// Executes the specified history requests
        /// </summary>
        /// <param name="requests">the history requests to execute</param>
        /// <returns>An enumerable of slice satisfying the specified history request</returns>
        public IEnumerable<Slice> History(IEnumerable<HistoryRequest> requests)
        {
            return History(requests, TimeZone);
        }

        private IEnumerable<Slice> History(IEnumerable<HistoryRequest> requests, DateTimeZone timeZone)
        {
            return ((IAlgorithm)this).HistoryProvider.GetHistory(requests, timeZone);
        }

        /// <summary>
        /// Set the maximum number of assets allowable to ensure good memory usage / avoid linux killing job.
        /// </summary>
        /// <param name="minuteLimit">Maximum number of minute level assets the live mode can support with selected server</param>
        /// <param name="secondLimit">Maximum number of second level assets the live mode can support with selected server</param>
        /// /// <param name="tickLimit">Maximum number of tick level assets the live mode can support with selected server</param>
        /// <remarks>Sets the live behaviour of the algorithm including the selected server (ram) limits.</remarks>
        public void SetAssetLimits(int minuteLimit = 500, int secondLimit = 100, int tickLimit = 30)
        {
            if (!_locked)
            {
                Securities.SetLimits(minuteLimit, secondLimit, tickLimit);
            }
        }

        /// <summary>
        /// Add specified data to our data subscriptions. QuantConnect will funnel this data to the handle data routine.
        /// </summary>
        /// <param name="securityType">MarketType Type: Equity, Commodity, Future or FOREX</param>
        /// <param name="symbol">Symbol Reference for the MarketType</param>
        /// <param name="resolution">Resolution of the Data Required</param>
        /// <param name="fillDataForward">When no data available on a tradebar, return the last data that was generated</param>
        /// <param name="extendedMarketHours">Show the after market data as well</param>
        public void AddSecurity(SecurityType securityType, Symbol symbol, Resolution resolution = Resolution.Minute, bool fillDataForward = true, bool extendedMarketHours = false)
        {
            AddSecurity(securityType, symbol, resolution, fillDataForward, 0, extendedMarketHours);
        }

        /// <summary>
        /// Add specified data to required list. QC will funnel this data to the handle data routine.
        /// </summary>
        /// <param name="securityType">MarketType Type: Equity, Commodity, Future or FOREX</param>
        /// <param name="symbol">Symbol Reference for the MarketType</param>
        /// <param name="resolution">Resolution of the Data Required</param>
        /// <param name="fillDataForward">When no data available on a tradebar, return the last data that was generated</param>
        /// <param name="leverage">Custom leverage per security</param>
        /// <param name="extendedMarketHours">Extended market hours</param>
        /// <remarks> AddSecurity(SecurityType securityType, Symbol symbol, Resolution resolution, bool fillDataForward, decimal leverage, bool extendedMarketHours)</remarks>
        public void AddSecurity(SecurityType securityType, Symbol symbol, Resolution resolution, bool fillDataForward, decimal leverage, bool extendedMarketHours) 
        {
            AddSecurity(securityType, symbol, resolution, null, fillDataForward, leverage, extendedMarketHours);
        }

        /// <summary>
        /// Set a required SecurityType-symbol and resolution for algorithm
        /// </summary>
        /// <param name="securityType">SecurityType Enum: Equity, Commodity, FOREX or Future</param>
        /// <param name="symbol">Symbol Representation of the MarketType, e.g. AAPL</param>
        /// <param name="resolution">Resolution of the MarketType required: MarketData, Second or Minute</param>
        /// <param name="market">The market the requested security belongs to, such as 'usa' or 'fxcm'</param>
        /// <param name="fillDataForward">If true, returns the last available data even if none in that timeslice.</param>
        /// <param name="leverage">leverage for this security</param>
        /// <param name="extendedMarketHours">ExtendedMarketHours send in data from 4am - 8pm, not used for FOREX</param>
        public void AddSecurity(SecurityType securityType, Symbol symbol, Resolution resolution, string market, bool fillDataForward, decimal leverage, bool extendedMarketHours)
        {
            if (_locked)
            {
                throw new Exception("Algorithm.AddSecurity(): Cannot add another security after algorithm running.");
            }

            try
            {
                var security = SecurityManager.CreateSecurity(Portfolio, SubscriptionManager, _exchangeHoursProvider,
                    securityType, symbol, resolution, market,
                    fillDataForward, leverage, extendedMarketHours, false, false);

                //Add the symbol to Securities Manager -- manage collection of portfolio entities for easy access.
                Securities.Add(security.Symbol, security);
            }
            catch (Exception err)
            {
                Error("Algorithm.AddSecurity(): " + err.Message);
            }
        }

        /// <summary>
        /// AddData<typeparam name="T"/> a new user defined data source, requiring only the minimum config options.
        /// The data is added with a default time zone of NewYork (Eastern Daylight Savings Time)
        /// </summary>
        /// <param name="symbol">Key/Symbol for data</param>
        /// <param name="resolution">Resolution of the data</param>
        /// <remarks>Generic type T must implement base data</remarks>
        public void AddData<T>(Symbol symbol, Resolution resolution = Resolution.Minute)
            where T : BaseData, new()
        {
            if (_locked) return;

            //Add this new generic data as a tradeable security: 
            // Defaults:extended market hours"      = true because we want events 24 hours, 
            //          fillforward                 = false because only want to trigger when there's new custom data.
            //          leverage                    = 1 because no leverage on nonmarket data?
            AddData<T>(symbol, resolution, fillDataForward: false, leverage: 1m);
        }

        /// <summary>
        /// AddData<typeparam name="T"/> a new user defined data source, requiring only the minimum config options.
        /// The data is added with a default time zone of NewYork (Eastern Daylight Savings Time)
        /// </summary>
        /// <param name="symbol">Key/Symbol for data</param>
        /// <param name="resolution">Resolution of the Data Required</param>
        /// <param name="fillDataForward">When no data available on a tradebar, return the last data that was generated</param>
        /// <param name="leverage">Custom leverage per security</param>
        /// <remarks>Generic type T must implement base data</remarks>
        public void AddData<T>(Symbol symbol, Resolution resolution, bool fillDataForward, decimal leverage = 1.0m)
            where T : BaseData, new()
        {
            if (_locked) return;

            AddData<T>(symbol, resolution, TimeZones.NewYork, fillDataForward, leverage);
        }

        /// <summary>
        /// AddData<typeparam name="T"/> a new user defined data source, requiring only the minimum config options.
        /// </summary>
        /// <param name="symbol">Key/Symbol for data</param>
        /// <param name="resolution">Resolution of the Data Required</param>
        /// <param name="timeZone">Specifies the time zone of the raw data</param>
        /// <param name="fillDataForward">When no data available on a tradebar, return the last data that was generated</param>
        /// <param name="leverage">Custom leverage per security</param>
        /// <remarks>Generic type T must implement base data</remarks>
        public void AddData<T>(Symbol symbol, Resolution resolution, DateTimeZone timeZone, bool fillDataForward = false, decimal leverage = 1.0m)
            where T : BaseData, new()
        {
            if (_locked) return;

            //Add this to the data-feed subscriptions
            var config = SubscriptionManager.Add(typeof(T), SecurityType.Base, symbol, resolution, "usa", timeZone, true, fillDataForward, true, false);

            var exchangeHours = _exchangeHoursProvider.GetExchangeHours(config);

            //Add this new generic data as a tradeable security: 
            var security = new Security(exchangeHours, config, leverage);
            Securities.Add(symbol, security);
        }

        /// <summary>
        /// Send a debug message to the web console:
        /// </summary>
        /// <param name="message">Message to send to debug console</param>
        /// <seealso cref="Log"/>
        /// <seealso cref="Error(string)"/>
        public void Debug(string message)
        {
            if (!_liveMode && (message == "" || _previousDebugMessage == message)) return;
            _debugMessages.Add(message);
            _previousDebugMessage = message;
        }

        /// <summary>
        /// Added another method for logging if user guessed.
        /// </summary>
        /// <param name="message">String message to log.</param>
        /// <seealso cref="Debug"/>
        /// <seealso cref="Error(string)"/>
        public void Log(string message) 
        {
            if (!_liveMode && message == "") return;
            _logMessages.Add(message);
        }

        /// <summary>
        /// Send a string error message to the Console.
        /// </summary>
        /// <param name="message">Message to display in errors grid</param>
        /// <seealso cref="Debug"/>
        /// <seealso cref="Log"/>
        public void Error(string message)
        {
            if (!_liveMode && (message == "" || _previousErrorMessage == message)) return;
            _errorMessages.Add(message);
            _previousErrorMessage = message;
        }

        /// <summary>
        /// Send a string error message to the Console.
        /// </summary>
        /// <param name="error">Exception object captured from a try catch loop</param>
        /// <seealso cref="Debug"/>
        /// <seealso cref="Log"/>
        public void Error(Exception error)
        {
            var message = error.Message;
            if (!_liveMode && (message == "" || _previousErrorMessage == message)) return;
            _errorMessages.Add(message);
            _previousErrorMessage = message;
        }

        /// <summary>
        /// Terminate the algorithm after processing the current event handler.
        /// </summary>
        /// <param name="message">Exit message to display on quitting</param>
        public void Quit(string message = "") 
        {
            Debug("Quit(): " + message);
            _quit = true;
        }

        /// <summary>
        /// Set the Quit flag property of the algorithm.
        /// </summary>
        /// <remarks>Intended for internal use by the QuantConnect Lean Engine only.</remarks>
        /// <param name="quit">Boolean quit state</param>
        /// <seealso cref="Quit"/>
        /// <seealso cref="GetQuit"/>
        public void SetQuit(bool quit) 
        {
            _quit = quit;
        }

        /// <summary>
        /// Get the quit state of the algorithm
        /// </summary>
        /// <returns>Boolean true if set to quit event loop.</returns>
        /// <remarks>Intended for internal use by the QuantConnect Lean Engine only.</remarks>
        /// <seealso cref="Quit"/>
        /// <seealso cref="SetQuit"/>
        public bool GetQuit() 
        {
            return _quit;
        }

    }
}
