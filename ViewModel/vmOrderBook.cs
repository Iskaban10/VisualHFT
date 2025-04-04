﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using VisualHFT.Helpers;
using VisualHFT.Model;
using OxyPlot;
using OxyPlot.Axes;
using VisualHFT.Commons.Pools;
using AxisPosition = OxyPlot.Axes.AxisPosition;
using Prism.Mvvm;
using VisualHFT.Commons.Helpers;
using VisualHFT.Enums;
using VisualHFT.Commons.Model;


namespace VisualHFT.ViewModel
{
    public class vmOrderBook : BindableBase, IDisposable
    {
        private static readonly int _MAX_CHART_POINTS = 5000;
        private static readonly int _MAX_TRADES_RECORDS = 100;
        private readonly TimeSpan _MIN_UI_REFRESH_TS = TimeSpan.FromMilliseconds(60); //For the UI: do not allow less than this, since it is not noticeble for human eye

        
        private static class OrderBookSnapshotPool
        {
            // Create a pool for OrderBookSnapshot objects.
            public static readonly CustomObjectPool<OrderBookSnapshot> Instance = new CustomObjectPool<OrderBookSnapshot>(maxPoolSize: _MAX_CHART_POINTS + (int)(_MAX_CHART_POINTS*0.1));
        }


        private bool _disposed = false; // to track whether the object has been disposed
        private readonly object MTX_RealTimePricePlotModel = new object();

        private readonly object MTX_RealTimeSpreadModel = new object();
        private readonly object MTX_CummulativeChartModel = new object();
        private readonly object MTX_bidsGrid = new object();
        private readonly object MTX_asksGrid = new object();

        private readonly object MTX_ORDERBOOK = new object();
        private readonly object MTX_TRADES = new object();

        private Dictionary<string, Func<string, string, bool>> _dialogs;
        private ObservableCollection<string> _symbols;
        private string _selectedSymbol;
        private VisualHFT.ViewModel.Model.Provider _selectedProvider = null;
        private AggregationLevel _aggregationLevelSelection;

        private List<BookItem> _bidsGrid;
        private List<BookItem> _asksGrid;
        private CachedCollection<BookItem> _depthGrid;

        private ObservableCollection<VisualHFT.ViewModel.Model.Provider> _providers;

        private BookItem _AskTOB = new BookItem();
        private BookItem _BidTOB = new BookItem();
        private double _MidPoint;
        private double _Spread;
        private int _decimalPlaces;


        private readonly Model.BookItemPriceSplit _BidTOB_SPLIT = null;
        private readonly Model.BookItemPriceSplit _AskTOB_SPLIT = null;

        private double _lobImbalanceValue = 0;
        private int _switchView = 0;

        private UIUpdater uiUpdater;

        private readonly Stack<VisualHFT.Model.Trade> _realTimeTrades;
        private VisualHFT.Commons.Pools.CustomObjectPool<OxyPlot.Series.ScatterPoint> _objectPool_ScatterPoint;
        private HelperCustomQueue<OrderBookSnapshot> _QUEUE;
        private AggregatedCollection<OrderBookSnapshot> _AGGREGATED_LOB;


        private bool _MARKETDATA_AVAILABLE = false;
        private bool _TRADEDATA_AVAILABLE = false;

        private double _minScatterBubbleSize = double.MaxValue;
        private double _maxScatterBubbleSize = 0.0;
        private double _minScatterVisualSize = 1.0; // Example: Min marker radius of 1
        private double _maxScatterVisualSize = 12.0;

        public vmOrderBook(Dictionary<string, Func<string, string, bool>> dialogs)
        {
            this._dialogs = dialogs;
            RealTimePricePlotModel = new PlotModel();
            RealTimeSpreadModel = new PlotModel();
            CummulativeBidsChartModel = new PlotModel();
            CummulativeAsksChartModel = new PlotModel();

            _objectPool_ScatterPoint = new CustomObjectPool<OxyPlot.Series.ScatterPoint>(_MAX_CHART_POINTS * 1000);

            _QUEUE = new HelperCustomQueue<OrderBookSnapshot>($"<OrderBookSnapshot>_vmOrderBook", QUEUE_onReadAction, QUEUE_onErrorAction);


            _realTimeTrades = new Stack<VisualHFT.Model.Trade>();
            TradesDisplay = new ObservableCollection<Trade>();

            InitializeRealTimePriceChart();
            InitializeRealTimeSpreadChart();
            InitializeCummulativeCharts();

            lock(MTX_bidsGrid)
                _bidsGrid = new List<BookItem>();
            lock (MTX_asksGrid)
                _asksGrid = new List<BookItem>();
            _depthGrid = new CachedCollection<BookItem>((x, y) => y.Price.GetValueOrDefault().CompareTo(x.Price.GetValueOrDefault()));

            _symbols = new ObservableCollection<string>(HelperSymbol.Instance);
            _providers = VisualHFT.ViewModel.Model.Provider.CreateObservableCollection();
            AggregationLevels = new ObservableCollection<Tuple<string, AggregationLevel>>();
            foreach (AggregationLevel level in Enum.GetValues(typeof(AggregationLevel)))
            {
                //if (level >= AggregationLevel.Ms100) //do not load less than 100ms. In order to save resources, we cannot go lower than 100ms (//TODO: in the future we must include lower aggregation levels)
                    AggregationLevels.Add(new Tuple<string, AggregationLevel>(Helpers.HelperCommon.GetEnumDescription(level), level));
            }
            _aggregationLevelSelection = AggregationLevel.Ms100; //DEFAULT
            uiUpdater = new UIUpdater(uiUpdaterAction, _aggregationLevelSelection.ToTimeSpan().TotalMilliseconds);
            _AGGREGATED_LOB = new AggregatedCollection<OrderBookSnapshot>(_aggregationLevelSelection, _MAX_CHART_POINTS, x => x.LastUpdated, _AGGREGATED_LOB_OnAggregating);
            _AGGREGATED_LOB.OnRemoved += _AGGREGATED_LOB_OnRemoved;
            _AGGREGATED_LOB.OnAdded += _AGGREGATED_LOB_OnAdded;

            HelperSymbol.Instance.OnCollectionChanged += ALLSYMBOLS_CollectionChanged;
            HelperProvider.Instance.OnDataReceived += PROVIDERS_OnDataReceived;
            HelperProvider.Instance.OnStatusChanged += PROVIDERS_OnStatusChanged;

            HelperTrade.Instance.Subscribe(TRADES_OnDataReceived);
            HelperOrderBook.Instance.Subscribe(LIMITORDERBOOK_OnDataReceived);


            _BidTOB_SPLIT = new Model.BookItemPriceSplit();
            _AskTOB_SPLIT = new Model.BookItemPriceSplit();

            RaisePropertyChanged(nameof(Providers));
            RaisePropertyChanged(nameof(BidTOB_SPLIT));
            RaisePropertyChanged(nameof(AskTOB_SPLIT));
            RaisePropertyChanged(nameof(TradesDisplay));

            SwitchView = 0;


        }

        ~vmOrderBook()
        {
            Dispose(false);
        }

        private void InitializeRealTimePriceChart()
        {
            RealTimePricePlotModel.DefaultFontSize = 8.0;
            RealTimePricePlotModel.Title = "";
            RealTimePricePlotModel.TitleColor = OxyColors.White;
            RealTimePricePlotModel.PlotAreaBorderColor = OxyColors.White;
            RealTimePricePlotModel.PlotAreaBorderThickness = new OxyThickness(0);
            RealTimePricePlotModel.EdgeRenderingMode = EdgeRenderingMode.PreferSpeed;
            var xAxis = new OxyPlot.Axes.DateTimeAxis()
            {
                Position = AxisPosition.Bottom,
                StringFormat = "HH:mm:ss", // Format time as hours:minutes:seconds
                IntervalType = DateTimeIntervalType.Auto, // Automatically determine the appropriate interval type (seconds, minutes, hours)
                MinorIntervalType = DateTimeIntervalType.Auto, // Automatically determine the appropriate minor interval type
                IntervalLength = 80, // Determines how much space each interval takes up, adjust as necessary
                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                AxislineStyle = LineStyle.Solid,
                IsAxisVisible = false,
                IsPanEnabled = false,
                IsZoomEnabled = false,
            };

            var yAxis = new OxyPlot.Axes.LinearAxis()
            {
                Position = AxisPosition.Left,
                StringFormat = "N",

                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                IsPanEnabled = false,
                IsZoomEnabled = false,
                IsAxisVisible = true
            };

            // Add a color axis to map quantity to color
            var RedColorAxis = new LinearColorAxis
            {
                Position = AxisPosition.Right,
                Palette = OxyPalette.Interpolate(10, OxyColors.Pink, OxyColors.DarkRed),
                Minimum = 1,
                Maximum = 100,
                Key = "RedColorAxis",
                IsAxisVisible = false
            };

            var GreenColorAxis = new LinearColorAxis
            {
                Position = AxisPosition.Right,
                Palette = OxyPalette.Interpolate(10, OxyColors.LightGreen, OxyColors.DarkGreen),
                Minimum = 1,
                Maximum = 100,
                Key = "GreenColorAxis",
                IsAxisVisible = false
            };


            RealTimePricePlotModel.Axes.Add(xAxis);
            RealTimePricePlotModel.Axes.Add(yAxis);
            RealTimePricePlotModel.Axes.Add(RedColorAxis);
            RealTimePricePlotModel.Axes.Add(GreenColorAxis);


            //Add MID-PRICE Serie
            var lineMidPrice = new OxyPlot.Series.LineSeries
            {
                Title = "MidPrice",
                MarkerType = MarkerType.None,
                StrokeThickness = 2,
                LineStyle = LineStyle.Solid,
                Color = OxyColors.Gray,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
            };
            var lineAsk = new OxyPlot.Series.LineSeries
            {
                Title = "Ask",
                MarkerType = MarkerType.None,
                StrokeThickness = 6,
                LineStyle = LineStyle.Solid,
                Color = OxyColors.Red,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
            };
            var lineBid = new OxyPlot.Series.LineSeries
            {
                Title = "Bid",
                MarkerType = MarkerType.None,
                StrokeThickness = 6,
                LineStyle = LineStyle.Solid,
                Color = OxyColors.Green,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
            };
            //SCATTER SERIES
            var scatterAsks = new OxyPlot.Series.ScatterSeries
            {
                Title = "ScatterAsks",
                ColorAxisKey = "RedColorAxis",
                MarkerType = MarkerType.Circle,
                MarkerStrokeThickness = 0,
                //MarkerStroke = OxyColors.DarkRed,
                MarkerStroke = OxyColors.Transparent,
                //MarkerFill = OxyColor.Parse("#80FF0000"),
                MarkerSize = 10,
                RenderInLegend = false,
                Selectable = false,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
                BinSize = 10 //smoothing the draw for speed performance
            };
            var scatterBids = new OxyPlot.Series.ScatterSeries
            {
                Title = "ScatterBids",
                ColorAxisKey = "GreenColorAxis",
                MarkerType = MarkerType.Circle,
                MarkerStroke = OxyColors.Transparent,
                //MarkerStroke = OxyColors.Green,
                MarkerStrokeThickness = 0,
                //MarkerFill = OxyColor.Parse("#8000FF00"),
                MarkerSize = 10,
                RenderInLegend = false,
                Selectable = false,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
                BinSize = 10 //smoothing the draw for speed performance
            };


            // do not change the order of adding these series (The overlap between them will depend on the order they have been added)
            RealTimePricePlotModel.Series.Add(scatterBids);
            RealTimePricePlotModel.Series.Add(scatterAsks);
            RealTimePricePlotModel.Series.Add(lineMidPrice);
            RealTimePricePlotModel.Series.Add(lineAsk);
            RealTimePricePlotModel.Series.Add(lineBid);

        }
        private void InitializeRealTimeSpreadChart()
        {
            RealTimeSpreadModel.DefaultFontSize = 8.0;
            RealTimeSpreadModel.Title = "";
            RealTimeSpreadModel.TitleColor = OxyColors.White;
            RealTimeSpreadModel.PlotAreaBorderColor = OxyColors.White;
            RealTimeSpreadModel.PlotAreaBorderThickness = new OxyThickness(0);

            var xAxis = new OxyPlot.Axes.DateTimeAxis
            {
                Position = AxisPosition.Bottom,
                StringFormat = "HH:mm:ss", // Format time as hours:minutes:seconds
                IntervalType = DateTimeIntervalType.Auto, // Automatically determine the appropriate interval type (seconds, minutes, hours)
                MinorIntervalType = DateTimeIntervalType.Auto, // Automatically determine the appropriate minor interval type
                IntervalLength = 80, // Determines how much space each interval takes up, adjust as necessary
                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                AxislineStyle = LineStyle.Solid,
                IsPanEnabled = false,
                IsZoomEnabled = false,
            };

            var yAxis = new OxyPlot.Axes.LinearAxis()
            {
                Position = AxisPosition.Left,
                StringFormat = "N",
                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                IsPanEnabled = false,
                IsZoomEnabled = false
            };
            RealTimeSpreadModel.Axes.Add(xAxis);
            RealTimeSpreadModel.Axes.Add(yAxis);



            //Add MID-PRICE Serie
            var lineSpreadSeries = new OxyPlot.Series.LineSeries
            {
                Title = "Spread",
                MarkerType = MarkerType.None,
                StrokeThickness = 4,
                LineStyle = LineStyle.Solid,
                Color = OxyColors.Blue,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,

            };

            RealTimeSpreadModel.Series.Add(lineSpreadSeries);
        }
        private void InitializeCummulativeCharts()
        {
            CummulativeBidsChartModel.DefaultFontSize = 8.0;
            CummulativeBidsChartModel.Title = "";
            CummulativeBidsChartModel.TitleColor = OxyColors.White;
            CummulativeBidsChartModel.PlotAreaBorderColor = OxyColors.White;
            CummulativeBidsChartModel.PlotAreaBorderThickness = new OxyThickness(0);
            CummulativeAsksChartModel.DefaultFontSize = 8.0;
            CummulativeAsksChartModel.Title = "";
            CummulativeAsksChartModel.TitleColor = OxyColors.White;
            CummulativeAsksChartModel.PlotAreaBorderColor = OxyColors.White;
            CummulativeAsksChartModel.PlotAreaBorderThickness = new OxyThickness(0);

            var xAxis = new OxyPlot.Axes.LinearAxis
            {
                Position = AxisPosition.Bottom,
                StringFormat = "N", // Format time as hours:minutes:seconds
                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                AxislineStyle = LineStyle.Solid,
                IsPanEnabled = false,
                IsZoomEnabled = false
            };

            var yAxis = new OxyPlot.Axes.LinearAxis()
            {
                Position = AxisPosition.Left,
                StringFormat = "N",
                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                IsPanEnabled = false,
                IsZoomEnabled = false
            };
            var xAxis2 = new OxyPlot.Axes.LinearAxis
            {
                Position = AxisPosition.Bottom,
                StringFormat = "N", // Format time as hours:minutes:seconds
                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                AxislineStyle = LineStyle.Solid,
                IsPanEnabled = false,
                IsZoomEnabled = false
            };

            var yAxis2 = new OxyPlot.Axes.LinearAxis()
            {
                Position = AxisPosition.Right,
                StringFormat = "N",
                FontSize = 8,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                IsPanEnabled = false,
                IsZoomEnabled = false
            };
            CummulativeBidsChartModel.Axes.Add(xAxis);
            CummulativeBidsChartModel.Axes.Add(yAxis);

            CummulativeAsksChartModel.Axes.Add(xAxis2);
            CummulativeAsksChartModel.Axes.Add(yAxis2);


            //AREA Series
            var areaSpreadSeriesBids = new OxyPlot.Series.TwoColorAreaSeries()
            {
                Title = "",
                MarkerType = MarkerType.None,
                StrokeThickness = 5,
                LineStyle = LineStyle.Solid,
                Color = OxyColors.LightGreen,
                Fill = OxyColors.DarkGreen,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
            };
            var areaSpreadSeriesAsks = new OxyPlot.Series.TwoColorAreaSeries()
            {
                Title = "",
                MarkerType = MarkerType.None,
                StrokeThickness = 5,
                LineStyle = LineStyle.Solid,
                Color = OxyColors.Pink,
                Fill = OxyColors.DarkRed,
                EdgeRenderingMode = EdgeRenderingMode.PreferSpeed,
            };

            CummulativeBidsChartModel.Series.Add(areaSpreadSeriesBids);
            CummulativeAsksChartModel.Series.Add(areaSpreadSeriesAsks);
        }
        private void uiUpdaterAction()
        {
            if (_selectedProvider == null || string.IsNullOrEmpty(_selectedSymbol))
                return;
            if (string.IsNullOrEmpty(_selectedSymbol) || _selectedSymbol == "-- All symbols --")
                return;

            if (_MARKETDATA_AVAILABLE)
            {
                // Perform property updates asynchronously
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    lock (MTX_ORDERBOOK)
                    {
                        _AskTOB_SPLIT?.RaiseUIThread();
                        _BidTOB_SPLIT?.RaiseUIThread();


                        RaisePropertyChanged(nameof(MidPoint));
                        RaisePropertyChanged(nameof(Spread));
                        RaisePropertyChanged(nameof(LOBImbalanceValue));

                    }
                });
                RaisePropertyChanged(nameof(Bids));
                RaisePropertyChanged(nameof(Asks));
                RaisePropertyChanged(nameof(Depth));


                //This is the most expensive calls. IT will freeze the UI thread if we don't de-couple
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    lock (MTX_RealTimePricePlotModel)
                        RealTimePricePlotModel.InvalidatePlot(true);
                    lock(MTX_RealTimeSpreadModel)
                        RealTimeSpreadModel.InvalidatePlot(true);
                    lock (MTX_CummulativeChartModel)
                    {
                        CummulativeBidsChartModel.InvalidatePlot(true);
                        CummulativeAsksChartModel.InvalidatePlot(true);
                    }
                });


                _MARKETDATA_AVAILABLE = false; //to avoid ui update when no new data is coming in
            }


            //TRADES
            if (_TRADEDATA_AVAILABLE)
            {
                lock (MTX_TRADES)
                {
                    while (_realTimeTrades.TryPop(out var itemToAdd))
                    {
                        TradesDisplay.Insert(0, itemToAdd);
                        if (TradesDisplay.Count > _MAX_TRADES_RECORDS)
                        {
                            TradesDisplay.RemoveAt(TradesDisplay.Count - 1);
                        }
                    }

                }
                _TRADEDATA_AVAILABLE = false; //to avoid the ui updates when no new data is coming in
            }


        }



        private void LIMITORDERBOOK_OnDataReceived(OrderBook e)
        {
            /*
             * ***************************************************************************************************
             * TRANSFORM the incoming object (decouple it)
             * DO NOT hold this call back, since other components depends on the speed of this specific call back.
             * DO NOT BLOCK
               * IDEALLY, USE QUEUES TO DECOUPLE
             * ***************************************************************************************************
             */
            if (_selectedProvider == null || _selectedProvider.ProviderID <= 0 || _selectedProvider.ProviderID != e?.ProviderID)
                return;
            if (string.IsNullOrEmpty(_selectedSymbol) || _selectedSymbol != e?.Symbol)
                return;


            e.CalculateMetrics();
            OrderBookSnapshot snapshot = OrderBookSnapshotPool.Instance.Get();
            // Initialize its state based on the master OrderBook.
            snapshot.UpdateFrom(e);
            // Enqueue for processing.
            _QUEUE.Add(snapshot);
        }

        private void _AGGREGATED_LOB_OnAdded(object? sender, OrderBookSnapshot e)
        {
            var lobItemToDisplay = _AGGREGATED_LOB[_AGGREGATED_LOB.Count() -1]; //if the new item is inserted, we need to get the previous one to update charts
            if (lobItemToDisplay == null)
                return;
            double sharedTS = lobItemToDisplay.LastUpdated.ToOADate();
            lock (MTX_RealTimeSpreadModel)
                AddPointToSpreadChart(ToDataPointSpread(lobItemToDisplay, sharedTS));

            double maxAsk = 0;
            double maxBid = 0;
            lock (MTX_CummulativeChartModel)
            {

                maxAsk = AddPointsToCumulativeAskVolumeChart(ToDataPointsCumulativeVolume(lobItemToDisplay.Asks, sharedTS));
                maxBid = AddPointsToCumulativeBidVolumeChart(ToDataPointsCumulativeVolume(lobItemToDisplay.Bids, sharedTS));
                var maxAll = Math.Max(maxBid, maxAsk);
                SetMaximumsToCumulativeBidVolumeCharts(maxAll);
                SetMaximumsToCumulativeAskVolumeCharts(maxAll);
            }

            lock (MTX_RealTimePricePlotModel)
                AddPointsToScatterPriceChart(
                    ToDataPointBestBid(lobItemToDisplay, sharedTS),
                    ToDataPointBestAsk(lobItemToDisplay, sharedTS),
                    ToDataPointMidPrice(lobItemToDisplay, sharedTS),
                    ToScatterPointsLevels(lobItemToDisplay.Bids.Where(x => x.Price >= _MidPoint * 0.99), sharedTS),
                    ToScatterPointsLevels(lobItemToDisplay.Asks.Where(x => x.Price <= _MidPoint * 1.01), sharedTS)
                );
            lock (MTX_ORDERBOOK)
                UpdateLocalValues(lobItemToDisplay);
            BidAskGridUpdate(lobItemToDisplay);

            _MARKETDATA_AVAILABLE = true; //to avoid ui update when no new data is coming in

        }
        private void _AGGREGATED_LOB_OnRemoved(object? sender, int index)
        { 
            //for current snapshot, make sure to return to the pool 
            OrderBookSnapshotPool.Instance.Return(_AGGREGATED_LOB[index]);

            //remove last points on the chart
            lock (MTX_RealTimeSpreadModel)
                RemoveLastPointToSpreadChart();
            lock (MTX_RealTimePricePlotModel)
                RemoveLastPointsToScatterChart();
        }
        private void _AGGREGATED_LOB_OnAggregating(OrderBookSnapshot existing, OrderBookSnapshot newItem)
        {
            //for current snapshot, the one we will replace with the new one, make sure to return to the pool 
            OrderBookSnapshotPool.Instance.Return(existing);
            
            //always keep latest snapshot. 
            existing = newItem;
        }

        private void QUEUE_onReadAction(OrderBookSnapshot ob)
        {
            //add item to the AggregatedCollection
            // when data is added       -> get prev item and Generate a new point for the chart
            // when data is removed     -> remove from the chart and return to the pool

            _AGGREGATED_LOB.Add(ob);
        }
        private void QUEUE_onErrorAction(Exception ex)
        {
            Console.WriteLine("Error in queue processing: " + ex.Message);
            //throw ex;
        }



        private DataPoint ToDataPointBestBid(OrderBookSnapshot lob, double sharedTS)
        {
            return new DataPoint(sharedTS, lob.Bids[0].Price.Value);
        }
        private DataPoint ToDataPointBestAsk(OrderBookSnapshot lob, double sharedTS)
        {
            return new DataPoint(sharedTS, lob.Asks[0].Price.Value);
        }
        private DataPoint ToDataPointMidPrice(OrderBookSnapshot lob, double sharedTS)
        {
            return new DataPoint(sharedTS, lob.MidPrice);
        }
        private DataPoint ToDataPointSpread(OrderBookSnapshot lob, double sharedTS)
        {
            return new DataPoint(sharedTS, lob.Spread);
        }
        private IEnumerable<OxyPlot.Series.ScatterPoint> ToScatterPointsLevels(IEnumerable<BookItem> lobList, double sharedTS)
        {
            if (lobList == null || !lobList.Any())
            {
                return [];
            }
            _minScatterBubbleSize = Math.Min(_minScatterBubbleSize,  lobList.Min(x => x.Size.Value));
            _maxScatterBubbleSize = Math.Max(_maxScatterBubbleSize, lobList.Max(x => x.Size.Value));
            double bookSizeRange = _maxScatterBubbleSize - _minScatterBubbleSize;
            double visualSizeRange = _maxScatterVisualSize - _minScatterVisualSize;

            var scatterPoints = new List<OxyPlot.Series.ScatterPoint>(lobList.Count());
            foreach (var lob in lobList)
            {
                double currentBookSize = lob.Size.Value;
                double visualSize;

                // --- 3. Linear Scaling Formula ---
                visualSize = _minScatterVisualSize + ((currentBookSize - _minScatterBubbleSize) / bookSizeRange) * visualSizeRange;
                // Clamp visual size just in case of floating point inaccuracies
                visualSize = Math.Max(_minScatterVisualSize, Math.Min(_maxScatterVisualSize, visualSize));


                if (lob.Price > 0 && lob.Size != 0)
                {
                    var newScatter = _objectPool_ScatterPoint.Get();
                    newScatter.X = sharedTS;
                    newScatter.Y = lob.Price.Value;
                    newScatter.Size = visualSize;
                    newScatter.Value = lob.Size.Value;
                    scatterPoints.Add(newScatter);
                }
            }
            return scatterPoints;
        }
        private IEnumerable<DataPoint> ToDataPointsCumulativeVolume(List<BookItem> lobList, double sharedTS)
        {
            var retItems = new List<DataPoint>(lobList.Count);
            double cumulativeVol = 0;
            foreach (var level in lobList)
            {
                if (level.Price.HasValue && level.Price.Value > 0 && level.Size.HasValue && level.Size.Value > 0)
                {
                    cumulativeVol += level.Size.Value;
                    retItems.Add(new DataPoint(level.Price.Value, cumulativeVol));
                }
            }
            return retItems;
        }
        private void AddPointToSpreadChart(DataPoint spreadPoint)
        {
            if (RealTimeSpreadModel.Series[0] is OxyPlot.Series.LineSeries _spreadSeries)
            {
                _spreadSeries.Points.Add(spreadPoint);
            }
        }
        private double AddPointsToCumulativeAskVolumeChart(IEnumerable<DataPoint> cumulativeAskPoints)
        {
            //get series
            var _cumSeriesAsks = CummulativeAsksChartModel.Series[0] as OxyPlot.Series.TwoColorAreaSeries;
            //clear current values
            _cumSeriesAsks?.Points.Clear();

            //ADD POINTS
            foreach (var item in cumulativeAskPoints)
            {
                _cumSeriesAsks?.Points.Add(item);
            }
            return cumulativeAskPoints?.LastOrDefault().Y ?? 0;
        }
        private double AddPointsToCumulativeBidVolumeChart(IEnumerable<DataPoint> cumulativeBidPoints)
        {
            //get series
            var _cumSeriesBids = CummulativeBidsChartModel.Series[0] as OxyPlot.Series.TwoColorAreaSeries;
            //clear current values
            _cumSeriesBids?.Points.Clear();

            //ADD POINTS
            foreach (var item in cumulativeBidPoints)
            {
                _cumSeriesBids?.Points.Add(item);
            }
            return cumulativeBidPoints?.LastOrDefault().Y ?? 0;
        }
        private void SetMaximumsToCumulativeBidVolumeCharts(double maxCumulativeVol)
        {
            var _cumSeriesBids = CummulativeBidsChartModel.Series[0] as OxyPlot.Series.TwoColorAreaSeries;
            if (_cumSeriesBids?.YAxis != null)
            {
                _cumSeriesBids.YAxis.Maximum = maxCumulativeVol;
            }
        }
        private void SetMaximumsToCumulativeAskVolumeCharts(double maxCumulativeVol)
        {
            var _cumSeriesAsks = CummulativeAsksChartModel.Series[0] as OxyPlot.Series.TwoColorAreaSeries;
            if (_cumSeriesAsks?.YAxis != null)
            {
                _cumSeriesAsks.YAxis.Maximum = maxCumulativeVol;
            }
        }
        private void AddPointsToScatterPriceChart(DataPoint bidPricePoint, 
            DataPoint askPricePoint,
            DataPoint midPricePoint,
            IEnumerable<OxyPlot.Series.ScatterPoint> bidLevelPoints,
            IEnumerable<OxyPlot.Series.ScatterPoint> askLevelPoints)
        {
            foreach (var serie in RealTimePricePlotModel.Series)
            {
                if (serie is OxyPlot.Series.LineSeries _serie)
                {
                    if (serie.Title == "MidPrice")
                        _serie.Points.Add(midPricePoint);
                    else if (serie.Title == "Ask")
                        _serie.Points.Add(askPricePoint);
                    else if (serie.Title == "Bid")
                        _serie.Points.Add(bidPricePoint);
                }
                else if (serie is OxyPlot.Series.ScatterSeries _scatter)
                {
                    if (_scatter.ColorAxis is LinearColorAxis colorAxis)
                    {
                        //we have defined min/max when normalizing the Size in "GenerateSinglePoint_RealTimePrice" method.
                        colorAxis.Minimum = 1;
                        colorAxis.Maximum = 10;
                    }
                    if (serie.Title == "ScatterAsks")
                    {
                        _scatter.Points.AddRange(askLevelPoints);
                    }
                    else if (serie.Title == "ScatterBids")
                    {
                        _scatter.Points.AddRange(bidLevelPoints);
                    }
                }
            }
        }
        private void UpdateLocalValues(OrderBookSnapshot orderBook)
        {
            _decimalPlaces = orderBook.PriceDecimalPlaces;

            _BidTOB = orderBook.GetTOB(true);
            _AskTOB = orderBook.GetTOB(false);
            _MidPoint = orderBook.MidPrice;
            _Spread = orderBook.Spread;
            _lobImbalanceValue = orderBook.ImbalanceValue;

            if (_AskTOB != null && _AskTOB.Price.HasValue && _AskTOB.Size.HasValue)
                _AskTOB_SPLIT.SetNumber(_AskTOB.Price.Value, _AskTOB.Size.Value, _decimalPlaces);
            if (_BidTOB != null && _BidTOB.Price.HasValue && _BidTOB.Size.HasValue)
                _BidTOB_SPLIT.SetNumber(_BidTOB.Price.Value, _BidTOB.Size.Value, _decimalPlaces);
        }
        private void RemoveLastPointToSpreadChart()
        {
            if (RealTimeSpreadModel.Series[0] is OxyPlot.Series.LineSeries _spreadSeries)
            {
                _spreadSeries.Points.RemoveAt(0);
            }
        }
        private void RemoveLastPointsToScatterChart()
        {
            double tsToRemove = 0;

            foreach (var serie in RealTimePricePlotModel.Series)
            {
                if (serie is OxyPlot.Series.LineSeries _serie)
                {
                    if (serie.Title == "MidPrice")
                    {
                        tsToRemove = _serie.Points[0].X;
                        _serie.Points.RemoveAt(0);
                    }
                    else if (serie.Title == "Ask")
                    {
                        tsToRemove = _serie.Points[0].X;
                        _serie.Points.RemoveAt(0);
                    }
                    else if (serie.Title == "Bid")
                    {
                        tsToRemove = _serie.Points[0].X;
                        _serie.Points.RemoveAt(0);
                    }
                }
            }
            if (tsToRemove == 0)
                return;
            foreach (var serie in RealTimePricePlotModel.Series)
            {
                if (serie is OxyPlot.Series.ScatterSeries _scatter)
                {
                    if (serie.Title == "ScatterAsks")
                    {
                        while (_scatter.Points[0].X == tsToRemove)
                        {
                            _objectPool_ScatterPoint.Return(_scatter.Points[0]);
                            _scatter.Points.RemoveAt(0);
                        }
                    }
                    else if (serie.Title == "ScatterBids")
                    {
                        while (_scatter.Points[0].X == tsToRemove)
                        {
                            _objectPool_ScatterPoint.Return(_scatter.Points[0]);
                            _scatter.Points.RemoveAt(0);
                        }
                    }
                }
            }
        }

        private void FailIfTrue(bool condition, string? msg)
        {
            if (condition)
            {
                throw new Exception(msg);
            }
        }

        private void Clear()
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                lock (MTX_TRADES)
                {
                    _realTimeTrades.Clear();
                    TradesDisplay.Clear();
                    RaisePropertyChanged(nameof(TradesDisplay));
                }
            });


            _QUEUE.Clear(); //make this outside the LOCK, otherwise we could run into a deadlock situation when calling back 
            //clean series
            lock (MTX_RealTimeSpreadModel)
                RealTimeSpreadModel?.Series.OfType<OxyPlot.Series.LineSeries>().ToList().ForEach(x => x.Points.Clear());
            lock (MTX_RealTimePricePlotModel)
            {
                RealTimePricePlotModel?.Series.OfType<OxyPlot.Series.LineSeries>().ToList()
                    .ForEach(x => x.Points.Clear());
                RealTimePricePlotModel?.Series.OfType<OxyPlot.Series.ScatterSeries>().ToList()
                    .ForEach(x => x.Points.Clear());
            }

            lock (MTX_CummulativeChartModel)
            {
                CummulativeAsksChartModel?.Series.OfType<OxyPlot.Series.TwoColorAreaSeries>().ToList().ForEach(x => x.Points.Clear());
                CummulativeBidsChartModel?.Series.OfType<OxyPlot.Series.TwoColorAreaSeries>().ToList().ForEach(x => x.Points.Clear());
            }

            lock (MTX_bidsGrid)
                _bidsGrid.Clear();
            lock (MTX_asksGrid)
                _asksGrid.Clear();

            lock (MTX_ORDERBOOK)
            {
                _AskTOB = new BookItem();
                _BidTOB = new BookItem();
                _MidPoint = 0;
                _Spread = 0;
                _depthGrid.Clear();

                _AskTOB_SPLIT.Clear();
                _BidTOB_SPLIT.Clear();

                _objectPool_ScatterPoint = new CustomObjectPool<OxyPlot.Series.ScatterPoint>(_MAX_CHART_POINTS * 1000);
                if (_AGGREGATED_LOB != null)
                {
                    _AGGREGATED_LOB.OnRemoved -= _AGGREGATED_LOB_OnRemoved;
                    _AGGREGATED_LOB.OnAdded -= _AGGREGATED_LOB_OnAdded;
                    _AGGREGATED_LOB.Dispose();
                }
                _AGGREGATED_LOB = new AggregatedCollection<OrderBookSnapshot>(_aggregationLevelSelection, _MAX_CHART_POINTS,
                    x => x.LastUpdated, _AGGREGATED_LOB_OnAggregating);
                _AGGREGATED_LOB.OnRemoved += _AGGREGATED_LOB_OnRemoved;
                _AGGREGATED_LOB.OnAdded += _AGGREGATED_LOB_OnAdded;
            }

            Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                uiUpdaterAction(); //update ui before the Timer starts again.
                if (uiUpdater != null)
                {
                    uiUpdater.Stop();
                    uiUpdater.Dispose();
                }

                var _aggregationForUI = _aggregationLevelSelection.ToTimeSpan();
                if (_aggregationForUI < _MIN_UI_REFRESH_TS)
                    _aggregationForUI = _MIN_UI_REFRESH_TS;
                uiUpdater = new UIUpdater(uiUpdaterAction, _aggregationForUI.TotalMilliseconds);
                uiUpdater.Start();
            });

        }


        /// <summary>
        /// Bids the ask grid update.
        /// Update our internal lists trying to re-use the current items on the list.
        /// Avoiding allocations as much as possible.
        /// </summary>
        /// <param name="orderBook">The order book.</param>
        private void BidAskGridUpdate(OrderBookSnapshot orderBook)
        {
            lock (MTX_asksGrid)
                _asksGrid = orderBook.Asks;
            lock(MTX_bidsGrid)
                _bidsGrid = orderBook.Bids;


            //commented out for now
            /*if (_asksGrid != null && _bidsGrid != null)
            {
                _depthGrid.Clear();
                foreach (var item in _asksGrid)
                    _depthGrid.Add(item);
                foreach (var item in _bidsGrid)
                    _depthGrid.Add(item);
            }*/
        }

        private void ALLSYMBOLS_CollectionChanged(object? sender, string e)
        {
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _symbols.Add(e);
            }));

        }
        private void TRADES_OnDataReceived(VisualHFT.Model.Trade e)
        {
            if (_selectedProvider == null || _selectedProvider.ProviderID <= 0 || _selectedProvider.ProviderID != e?.ProviderId)
                return;
            if (string.IsNullOrEmpty(_selectedSymbol) || _selectedSymbol != e?.Symbol)
                return;

            lock (MTX_TRADES)
            {
                _realTimeTrades.Push(e);
                _TRADEDATA_AVAILABLE = true;
            }
        }
        private void PROVIDERS_OnDataReceived(object? sender, VisualHFT.Model.Provider e)
        {
            if (_providers.All(x => x.ProviderCode != e.ProviderCode))
            {
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    var item = new ViewModel.Model.Provider(e);
                    if (_providers.All(x => x.ProviderCode != e.ProviderCode))
                        _providers.Add(item);
                    //if nothing is selected
                    if (_selectedProvider == null) //default provider must be the first who's Active
                        SelectedProvider = item;
                }));
            }
        }
        private void PROVIDERS_OnStatusChanged(object? sender, VisualHFT.Model.Provider e)
        {
            if (_selectedProvider == null || _selectedProvider.ProviderCode != e.ProviderCode)
                return;

            if (_selectedProvider.Status != e.Status)
            {
                _selectedProvider.Status = e.Status;
                Clear();
            }
        }

        public ObservableCollection<string> SymbolList => _symbols;
        public string SelectedSymbol
        {
            get => _selectedSymbol;
            set => SetProperty(ref _selectedSymbol, value, onChanged: () => Clear());
        }
        public VisualHFT.ViewModel.Model.Provider SelectedProvider
        {
            get => _selectedProvider;
            set => SetProperty(ref _selectedProvider, value, onChanged: () => Clear());
        }
        public string SelectedLayer { get; set; }
        public ObservableCollection<Tuple<string, AggregationLevel>> AggregationLevels { get; set; }

        public AggregationLevel AggregationLevelSelection
        {
            get => _aggregationLevelSelection;
            set => SetProperty(ref _aggregationLevelSelection, value, onChanged: () => Clear());
        }

        public ObservableCollection<VisualHFT.ViewModel.Model.Provider> Providers => _providers;
        public Model.BookItemPriceSplit BidTOB_SPLIT => _BidTOB_SPLIT;
        public Model.BookItemPriceSplit AskTOB_SPLIT => _AskTOB_SPLIT;

        public double LOBImbalanceValue => _lobImbalanceValue;
        public double MidPoint => _MidPoint;
        public double Spread => _Spread;

        public IEnumerable<BookItem> Asks
        {
            get
            {
                lock (MTX_asksGrid)
                    return _asksGrid;
            }
        }

        public IEnumerable<BookItem> Bids
        {
            get
            {
                lock (MTX_bidsGrid)
                    return _bidsGrid;
            }
        }

        public IEnumerable<BookItem> Depth => _depthGrid;
        public ObservableCollection<VisualHFT.Model.Trade> TradesDisplay { get; }

        public PlotModel RealTimePricePlotModel { get; set; }
        public PlotModel RealTimeSpreadModel { get; set; }
        public PlotModel CummulativeBidsChartModel { get; set; }
        public PlotModel CummulativeAsksChartModel { get; set; }


        public int SwitchView
        {
            get => _switchView;
            set => SetProperty(ref _switchView, value);
        }


        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    uiUpdater?.Stop();
                    uiUpdater?.Dispose();

                    HelperSymbol.Instance.OnCollectionChanged -= ALLSYMBOLS_CollectionChanged;
                    HelperProvider.Instance.OnDataReceived -= PROVIDERS_OnDataReceived;
                    HelperProvider.Instance.OnStatusChanged -= PROVIDERS_OnStatusChanged;
                    HelperOrderBook.Instance.Unsubscribe(LIMITORDERBOOK_OnDataReceived);
                    HelperTrade.Instance.Unsubscribe(TRADES_OnDataReceived);

                    _dialogs = null;
                    _realTimeTrades?.Clear();
                    _depthGrid?.Clear();
                    _bidsGrid?.Clear();
                    _asksGrid?.Clear();
                    _providers?.Clear();
                    _objectPool_ScatterPoint?.Dispose();
                    OrderBookSnapshotPool.Instance.Dispose();
                    _QUEUE?.Dispose();

                    if (_AGGREGATED_LOB != null)
                    {
                        _AGGREGATED_LOB.OnRemoved -= _AGGREGATED_LOB_OnRemoved;
                        _AGGREGATED_LOB.OnAdded -= _AGGREGATED_LOB_OnAdded;
                    }
                    _AGGREGATED_LOB?.Dispose();
                }
                _disposed = true;
            }
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

    }
}
