﻿using Gemini.Net.Clients;
using Gemini.Net.Models;
using MarketConnectors.Gemini.Model;
using MarketConnectors.Gemini.UserControls;
using MarketConnectors.Gemini.ViewModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using VisualHFT.Commons.Helpers;
using VisualHFT.Commons.Interfaces;
using VisualHFT.Commons.Model;
using VisualHFT.Commons.PluginManager;
using VisualHFT.DataRetriever.DataParsers;
using VisualHFT.Enums;
using VisualHFT.PluginManager;
using VisualHFT.UserSettings;
using Websocket.Client;
using JsonSerializerOptions = System.Text.Json.JsonSerializerOptions;
using OrderBook = VisualHFT.Model.OrderBook;
using Trade = VisualHFT.Model.Trade;

namespace MarketConnectors.Gemini
{
    public class GeminiPlugin : BasePluginDataRetriever, IDataRetrieverTestable
    {
        private new bool _disposed = false; // to track whether the object has been disposed
        GeminiSubscription geminiSubscription = new GeminiSubscription();

        private Timer _heartbeatTimer;
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private Dictionary<string, VisualHFT.Model.OrderBook> _localOrderBooks = new Dictionary<string, VisualHFT.Model.OrderBook>();
        private Dictionary<string, VisualHFT.Model.Order> _localUserOrders = new Dictionary<string, VisualHFT.Model.Order>();
        private HelperCustomQueue<MarketUpdate> _eventBuffers;


        private PlugInSettings? _settings;
        public override string Name { get; set; } = "Gemini";
        public override string Version { get; set; } = "1.0.0";
        public override string Description { get; set; } = "Connects to Gemini.";
        public override string Author { get; set; } = "VisualHFT";
        public override ISetting Settings { get => _settings; set => _settings = (PlugInSettings)value; }
        public override Action? CloseSettingWindow { get; set; }

        private IDataParser _parser;
        WebsocketClient? _socketClient;
        WebsocketClient? _userOrderEvents;
        GeminiHttpClient geminiHttpClient;
        private bool isReconnecting = false;

        public GeminiPlugin()
        {

            _parser = new JsonParser();

            GeminiSubscription geminiSubscription = new GeminiSubscription();
            geminiSubscription.subscriptions = new List<Subscription>();

            geminiHttpClient = new GeminiHttpClient();
            SetReconnectionAction(InternalStartAsync);
        }

        ~GeminiPlugin()
        {
            Dispose(false);
        }


        public override async Task StartAsync()
        {
            await base.StartAsync();//call the base first 
            try
            {

                await InternalStartAsync();
            }
            catch (Exception ex)
            {
                var _error = ex.Message;
                LogException(ex, _error);
                if (_error.IndexOf("[CantConnectError]") > -1)
                {
                    Status = ePluginStatus.STOPPED_FAILED;
                    HelperNotificationManager.Instance.AddNotification(this.Name, _error, HelprNorificationManagerTypes.ERROR, HelprNorificationManagerCategories.PLUGINS);

                    await ClearAsync();
                    RaiseOnDataReceived(new List<VisualHFT.Model.OrderBook>());
                    RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED_FAILED));
                }
                else
                {
                    await HandleConnectionLost(_error, ex);

                }
            }
            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
            Status = ePluginStatus.STARTED;

        }

        public async Task ClearAsync()
        {
            _heartbeatTimer?.Dispose();
            //CLEAR LOB
            if (_localOrderBooks != null)
            {
                foreach (var lob in _localOrderBooks)
                {
                    lob.Value?.Dispose();
                }
                _localOrderBooks.Clear();
            }
        }
        public async Task InternalStartAsync()
        {
            await ClearAsync();

            _eventBuffers = new HelperCustomQueue<MarketUpdate>($"<MarketUpdate>_{this.Name}", eventBuffers_onReadAction, eventBuffers_onErrorAction);

            await InitializeSnapshotAsync();
            await InitializeDeltasAsync();

            // Initialize the timer
            _heartbeatTimer = new Timer(CheckConnectionStatus, null, TimeSpan.Zero, TimeSpan.FromSeconds(5)); // Check every 5 seconds

            await InitializeUserPrivateOrders();
        }

        private void eventBuffers_onReadAction(MarketUpdate eventData)
        {
            var symbol = GetNormalizedSymbol(eventData.Symbol);
            UpdateOrderBook(eventData, symbol, DateTime.Now);

        }
        private void eventBuffers_onErrorAction(Exception ex)
        {
            var _error = $"Will reconnect. Unhandled error in the Market Data Queue: {ex.Message}";
            LogException(ex, _error);
            Task.Run(async () => await HandleConnectionLost(_error, ex));
        }


        private async Task InitializeDeltasAsync()
        {
            try
            {
                geminiSubscription.subscriptions = new List<Subscription>();
                geminiSubscription.subscriptions.Add(new Subscription()
                {
                    symbols = GetAllNonNormalizedSymbols()
                });

                var exitEvent = new ManualResetEvent(false);
                var url = new Uri(_settings.WebSocketHostName);
                _socketClient = new WebsocketClient(url);

                _socketClient.ReconnectTimeout = TimeSpan.FromSeconds(10);
                _socketClient.ReconnectionHappened.Subscribe(info =>
                {
                    if (info.Type == ReconnectionType.Error)
                    {
                        RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
                        Status = ePluginStatus.STOPPED_FAILED;
                    }
                    else if (info.Type == ReconnectionType.Initial)
                    {
                        foreach (var symbol in GetAllNonNormalizedSymbols())
                        {
                            var normalizedSymbol = GetNormalizedSymbol(symbol);
                            if (!_localOrderBooks.ContainsKey(normalizedSymbol))
                            {
                                _localOrderBooks.Add(normalizedSymbol, null);
                            }
                        }
                    }
                });
                _socketClient.DisconnectionHappened.Subscribe(disconnected =>
                {
                    Status = ePluginStatus.STOPPED;
                    if (isReconnecting)
                        return;
                    if (disconnected.CloseStatus == WebSocketCloseStatus.NormalClosure)
                    {
                        RaiseOnDataReceived(new List<VisualHFT.Model.OrderBook>());
                        RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED));

                    }
                    else
                    {
                        var _error = $"Will reconnect. Unhandled error while receiving delta market data.";
                        LogException(disconnected?.Exception, _error);
                        if (!isReconnecting)
                        {
                            isReconnecting = true;
                            HandleConnectionLost(disconnected.CloseStatusDescription, disconnected.Exception);
                            isReconnecting = false;
                        }
                    }
                });
                _socketClient.MessageReceived.Subscribe(async msg =>
                {
                    try
                    {
                        string data = msg.ToString();
                        HandleMessage(data, DateTime.Now);
                        RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));

                    }
                    catch (Exception ex)
                    {

                        var _error = $"Will reconnect. Unhandled error while receiving delta market data.";
                        LogException(ex, _error);
                        if (!isReconnecting)
                        {
                            isReconnecting = true;
                            await HandleConnectionLost(_error, ex);
                            isReconnecting = false;
                        }
                    }
                });
                try
                {
                    await _socketClient.Start();
                    log.Info($"Plugin has successfully started.");
                    RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
                    string jsonToSubscribe = JsonConvert.SerializeObject(geminiSubscription);
                    _socketClient.Send(jsonToSubscribe);
                    Status = ePluginStatus.STARTED;
                }
                catch (Exception ex)
                {
                    var _error = ex.Message;
                    LogException(ex, _error);
                    if (!isReconnecting)
                    {
                        isReconnecting = true;
                        await HandleConnectionLost(_error, ex);
                        isReconnecting = false;
                    }
                }
            }
            catch (Exception ex)
            {
                var _error = ex.Message;
                LogException(ex, _error);
                if (!isReconnecting)
                {
                    isReconnecting = true;
                    await HandleConnectionLost(_error, ex);
                    isReconnecting = false;
                }
            }
            CancellationTokenSource source = new CancellationTokenSource();
        }
        private string CreateSignature(string b64)
        {
            using (var hmac = new HMACSHA384(Encoding.UTF8.GetBytes(_settings.ApiSecret)))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(b64));
                return BitConverter.ToString(hash).Replace("-", "").ToLower();
            }
        }
        private async Task InitializeUserPrivateOrders()
        {
            if (!string.IsNullOrEmpty(_settings.ApiKey) && !string.IsNullOrEmpty(_settings.ApiSecret))
            {
                try
                {
                    var payload = new
                    {
                        request = "/v1/order/events",
                        nonce = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5
                    };

                    string payloadstring = JsonConvert.SerializeObject(payload);
                    var encodedPayload = Encoding.UTF8.GetBytes(payloadstring);
                    var b64 = Convert.ToBase64String(encodedPayload);
                    var signature = CreateSignature(b64);

                    var factory = new Func<ClientWebSocket>(() =>
                    {
                        var client = new ClientWebSocket
                        {
                            Options =
                        {
                        KeepAliveInterval = TimeSpan.FromSeconds(30),
                        }
                        };
                        client.Options.SetRequestHeader("X-GEMINI-APIKEY", _settings.ApiKey);
                        client.Options.SetRequestHeader("X-GEMINI-PAYLOAD", b64);
                        client.Options.SetRequestHeader("X-GEMINI-SIGNATURE", signature);

                        return client;
                    });

                    var exitEvent = new ManualResetEvent(false);
                    _userOrderEvents = new WebsocketClient(new Uri(_settings.WebSocketHostName_UserOrder), factory);
                    _userOrderEvents.ReconnectTimeout = TimeSpan.FromSeconds(10);
                    _userOrderEvents.ReconnectionHappened.Subscribe(info =>
                    {

                        if (info.Type == ReconnectionType.Error)
                        {
                            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
                            Status = ePluginStatus.STOPPED_FAILED;
                        }
                        else if (info.Type == ReconnectionType.Initial)
                        {

                        }

                    });
                    _userOrderEvents.DisconnectionHappened.Subscribe(disconnected =>
                    {
                        Status = ePluginStatus.STOPPED;
                        if (isReconnecting)
                            return;
                        if (disconnected.CloseStatus == WebSocketCloseStatus.NormalClosure)
                        {
                            RaiseOnDataReceived(new List<VisualHFT.Model.OrderBook>());
                            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED));
                            base.StopAsync();
                        }
                        else
                        {
                            var _error = $"Will reconnect. Unhandled error while receiving delta market data.";
                            LogException(disconnected?.Exception, _error);
                            if (!isReconnecting)
                            {
                                isReconnecting = true;
                                HandleConnectionLost(disconnected.CloseStatusDescription, disconnected.Exception);
                                isReconnecting = false;
                            }
                        }
                    });
                    _userOrderEvents.MessageReceived.Subscribe(async msg =>
                    {
                        string data = msg.ToString();
                        HandleUserOrderMessage(data);
                    });
                    try
                    {

                        await _userOrderEvents.Start();

                        log.Info($"Plugin has successfully started.");
                    }
                    catch (Exception ex)
                    {
                        LogException(ex, "userOrderEvents.MessageReceived");
                    }
                }
                catch (Exception ex)
                {
                    LogException(ex, "socketClient.MessageReceived");
                }
                CancellationTokenSource source = new CancellationTokenSource();
            }
        }
        private async Task InitializeSnapshotAsync()
        {
            foreach (var symbol in GetAllNonNormalizedSymbols())
            {
                var normalizedSymbol = GetNormalizedSymbol(symbol);
                if (!_localOrderBooks.ContainsKey(normalizedSymbol))
                {
                    _localOrderBooks.Add(normalizedSymbol, null);
                }
                var response = await geminiHttpClient.InitializeSnapshotAsync(symbol, _settings.DepthLevels);
                if (response != null)
                {
                    _localOrderBooks[normalizedSymbol] = ToOrderBookModel(response, normalizedSymbol);
                }

            }
        }

        public override async Task StopAsync()
        {
            Status = ePluginStatus.STOPPING;
            log.Info($"{this.Name} is stopping.");

            await ClearAsync();
            if (_socketClient != null)
            {
                await _socketClient.Stop(WebSocketCloseStatus.NormalClosure, "Manual CLosing");
                _socketClient.Dispose();
            }
            RaiseOnDataReceived(new List<VisualHFT.Model.OrderBook>());
            RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED));
            await base.StopAsync();
        }


        private VisualHFT.Model.OrderBook ToOrderBookModel(InitialResponse data, string symbol)
        {
            var identifiedPriceDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.asks.Select(x => x.price));

            var lob = new VisualHFT.Model.OrderBook(symbol, identifiedPriceDecimalPlaces, _settings.DepthLevels);
            lob.ProviderID = _settings.Provider.ProviderID;
            lob.ProviderName = _settings.Provider.ProviderName;
            lob.SizeDecimalPlaces = RecognizeDecimalPlacesAutomatically(data.asks.Select(x => x.amount));
            lob.FilterBidAskByMaxDepth = true;


            /*
                Initialize the Limit Order Book "only" in this method.
                Do not load the snapshot data, since it will be given at deltas subscription in the first message.
                So, just initialize the LOB hete
             */


            /*
            var _asks = new List<VisualHFT.Model.BookItem>();
            var _bids = new List<VisualHFT.Model.BookItem>();
            data.asks.ForEach(x =>
            {
                _asks.Add(new VisualHFT.Model.BookItem()
                {
                    IsBid = false,
                    Price = (double)x.price,
                    Size = (double)x.amount,
                    LocalTimeStamp = DateTime.Now,
                    ServerTimeStamp = DateTime.Now,
                    Symbol = lob.Symbol,
                    PriceDecimalPlaces = lob.PriceDecimalPlaces,
                    SizeDecimalPlaces = lob.SizeDecimalPlaces,
                    ProviderID = lob.ProviderID,
                    LayerName = "snapshot"
                });
            });
            data.bids.ForEach(x =>
            {
                _bids.Add(new VisualHFT.Model.BookItem()
                {
                    IsBid = true,
                    Price = (double)x.price,
                    Size = (double)x.amount,
                    LocalTimeStamp = DateTime.Now,
                    ServerTimeStamp = DateTime.Now,
                    Symbol = lob.Symbol,
                    PriceDecimalPlaces = lob.PriceDecimalPlaces,
                    SizeDecimalPlaces = lob.SizeDecimalPlaces,
                    ProviderID = lob.ProviderID,
                    LayerName = "snapshot"
                });
            });
            lob.LoadData(_asks.OrderBy(x => x.Price).Take(_settings.DepthLevels),_bids.OrderByDescending(x => x.Price).Take(_settings.DepthLevels));
            */
            return lob;
        }

        private void HandleUserOrderMessage(string data)
        {
            string message = data;
            if (!string.IsNullOrEmpty(message) && message.Length > 2)
            {
                JToken token = JToken.Parse(message);
                if (token is JObject)
                {
                    dynamic dataType = JsonConvert.DeserializeObject<dynamic>(message);

                    if (dataType.type == "initial")
                    {
                        ProcessUserOrderData(message);
                    }
                    else if (dataType.type == "subscription_ack")
                    {

                    }
                    else if (dataType.type == "heartbeat")
                    {


                    }
                    else
                    {
                        string ss = message;
                    }
                }
                else if (token is JArray)
                {
                    ProcessUserOrderData(message);
                }

            }
        }
        private void ProcessUserOrderData(string message)
        {
            List<UserOrderData> _dataType = JsonConvert.DeserializeObject<List<UserOrderData>>(message);

            if (_dataType != null && _dataType.Count > 0)
            {
                foreach (var item in _dataType)
                {

                    UpdateUserOrderBook(item);

                }
            }
        }

        private void UpdateUserOrderBook(UserOrderData item)
        {
            VisualHFT.Model.Order localuserOrder;
            if (!this._localUserOrders.ContainsKey(item.client_order_id))
            {
                localuserOrder = new VisualHFT.Model.Order();
                localuserOrder.ClOrdId = item.client_order_id;
                localuserOrder.Currency = GetNormalizedSymbol(item.symbol);
                localuserOrder.OrderID = item.order_id;
                localuserOrder.ProviderId = _settings!.Provider.ProviderID;
                localuserOrder.ProviderName = _settings.Provider.ProviderName;
                localuserOrder.CreationTimeStamp = DateTimeOffset.FromUnixTimeSeconds(item.timestamp).DateTime;
                localuserOrder.Quantity = item.original_amount;
                localuserOrder.PricePlaced = item.price;
                localuserOrder.Symbol = GetNormalizedSymbol(item.symbol);
                localuserOrder.FilledQuantity = item.executed_amount;
                localuserOrder.TimeInForce = eORDERTIMEINFORCE.GTC;

                if (!string.IsNullOrEmpty(item.behavior))
                {
                    if (item.behavior.ToLower().Equals("immediate-or-cancel"))
                    {
                        localuserOrder.TimeInForce = eORDERTIMEINFORCE.IOC;
                    }
                    else if (item.behavior.ToLower().Equals("fill-or-kill"))
                    {
                        localuserOrder.TimeInForce = eORDERTIMEINFORCE.FOK;
                    }
                    else if (item.behavior.ToLower().Equals("maker-or-cancel"))
                    {
                        localuserOrder.TimeInForce = eORDERTIMEINFORCE.MOK;
                    }
                }
                this._localUserOrders.Add(item.client_order_id, localuserOrder);
            }
            else
            {

                localuserOrder = this._localUserOrders[item.client_order_id];
            }
            localuserOrder.OrderType = eORDERTYPE.LIMIT;

            localuserOrder.Quantity = item.original_amount;
            if (item.order_type.ToLower().Equals("limit"))
            {
                localuserOrder.OrderType = eORDERTYPE.LIMIT;
            }
            else if (item.order_type.ToLower().Equals("exchange limit"))
            {
                localuserOrder.OrderType = eORDERTYPE.PEGGED;
            }
            else if (item.order_type.ToLower().Equals("market buy"))
            {
                localuserOrder.OrderType = eORDERTYPE.MARKET;
            }

            if (item.side.ToLower().Equals("sell"))
            {
                localuserOrder.BestAsk = item.price;
                localuserOrder.Side = eORDERSIDE.Sell;

            }
            else if (item.side.ToLower().Equals("buy"))
            {
                localuserOrder.PricePlaced = item.price;
                localuserOrder.BestBid = item.price;
                localuserOrder.Side = eORDERSIDE.Buy;

            }

            if (item.type.ToLower().Equals("accepted"))
            {
                localuserOrder.Status = eORDERSTATUS.NEW;
            }
            else if (item.type.ToLower().Equals("fill"))
            {
                localuserOrder.FilledQuantity = item.executed_amount;
                localuserOrder.Status = eORDERSTATUS.PARTIALFILLED;
            }
            else if (item.type.ToLower().Equals("closed"))
            {
                localuserOrder.Status = eORDERSTATUS.FILLED;
                if (item.is_cancelled)
                {
                    localuserOrder.Status = eORDERSTATUS.CANCELED;

                }
            }
            else if (item.type.ToLower().Equals("rejected"))
            {
                localuserOrder.Status = eORDERSTATUS.REJECTED;
            }
            else if (item.type.ToLower().Equals("cancelled"))
            {
                localuserOrder.Status = eORDERSTATUS.CANCELED;
            }
            else if (item.type.ToLower().Equals("cancel_rejected"))
            {
                localuserOrder.Status = eORDERSTATUS.CANCELED;
            }
            if (!string.IsNullOrEmpty(item.behavior))
            {
                if (item.behavior.ToLower().Equals("immediate-or-cancel"))
                {
                    localuserOrder.TimeInForce = eORDERTIMEINFORCE.IOC;
                }
                else if (item.behavior.ToLower().Equals("fill-or-kill"))
                {
                    localuserOrder.TimeInForce = eORDERTIMEINFORCE.FOK;
                }
                else if (item.behavior.ToLower().Equals("maker-or-cancel"))
                {
                    localuserOrder.TimeInForce = eORDERTIMEINFORCE.MOK;
                }
            }
            localuserOrder.LastUpdated = DateTime.Now;
            localuserOrder.FilledPercentage = Math.Round((100 / localuserOrder.Quantity) * localuserOrder.FilledQuantity, 2);
            RaiseOnDataReceived(localuserOrder);
        }


        private void UpdateOrderBook(MarketUpdate lob_update, string symbol, DateTime serverTime)
        {

            if (!_localOrderBooks.ContainsKey(symbol))
                return;
            var local_lob = _localOrderBooks[symbol];
            if (local_lob == null)//check if the order book is not initialized
            {
                local_lob = new OrderBook();
            }


            foreach (var item in lob_update.Changes)
            {
                bool isBid = item[0].ToLower().Equals("buy");
                double.TryParse(item[1], out double _price);
                double.TryParse(item[2], out double _qty);

                if (isBid)
                {
                    if (_qty == 0)
                    {
                        local_lob.DeleteLevel(new DeltaBookItem() { Symbol = symbol, Price = _price, IsBid = true, LocalTimeStamp = DateTime.Now, ServerTimeStamp = DateTime.Now, MDUpdateAction = eMDUpdateAction.Delete });
                    }
                    else
                    {
                        local_lob.AddOrUpdateLevel(new DeltaBookItem()
                        {
                            Symbol = symbol,
                            IsBid = true,
                            LocalTimeStamp = DateTime.Now,
                            ServerTimeStamp = serverTime,
                            MDUpdateAction = eMDUpdateAction.New,
                            Price = _price,
                            Size = _qty,
                        });
                    }
                }
                else
                {
                    if (_qty == 0)
                    {
                        local_lob.DeleteLevel(new DeltaBookItem() { Symbol = symbol, Price = _price, IsBid = false, LocalTimeStamp = DateTime.Now, ServerTimeStamp = DateTime.Now, MDUpdateAction = eMDUpdateAction.Delete });
                    }
                    else
                    {
                        local_lob.AddOrUpdateLevel(new DeltaBookItem()
                        {
                            Symbol = symbol,
                            IsBid = false,
                            LocalTimeStamp = DateTime.Now,
                            ServerTimeStamp = serverTime,
                            MDUpdateAction = eMDUpdateAction.New,
                            Price = _price,
                            Size = _qty,
                        });
                    }
                }
            }
            local_lob.LastUpdated = null; //Set to null since Gemini does not provide timstamp of their messages

            RaiseOnDataReceived(local_lob);

            if (lob_update.Trades != null)
            {
                foreach (var item in lob_update.Trades)
                {
                    if (item.Type == "trade")
                    {
                        UpdateTrades(item);
                    }
                    else
                    {
                        Console.WriteLine("Type not recognized " + item.Type);
                    }
                }
            }
        }

        private void UpdateTrades(MarketConnectors.Gemini.Model.Trade item)
        {

            RaiseOnDataReceived(new Trade()
            {
                Symbol = GetNormalizedSymbol(item.Symbol),
                Size = item.Quantity,
                Price = item.Price,
                IsBuy = item.Side.ToLower() == "buy",
                ProviderId = _settings.Provider.ProviderID,
                ProviderName = _settings.Provider.ProviderName,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(item.Timestamp).DateTime
            });
        }
        static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private void HandleMessage(string marketData, DateTime serverTime)
        {
            // heartbeat messages happen most often; skip any JSON work entirely
            if (marketData.Contains("\"type\":\"heartbeat\"", StringComparison.Ordinal))
            {
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
                return;
            }

            // level-2 updates
            if (marketData.Contains("\"type\":\"l2_updates\"", StringComparison.Ordinal))
            {
                var update = System.Text.Json.JsonSerializer.Deserialize<MarketUpdate>(marketData, s_jsonOptions);
                // normalize once, on the object you actually use
                update.Symbol = GetNormalizedSymbol(update.Symbol);
                _eventBuffers.Add(update);
                return;
            }

            // trades
            if (marketData.Contains("\"type\":\"trade\"", StringComparison.Ordinal))
            {
                var trade = System.Text.Json.JsonSerializer.Deserialize<MarketConnectors.Gemini.Model.Trade>(marketData, s_jsonOptions);
                UpdateTrades(trade);
                return;
            }

            // to catch weird payloads:
            throw new Exception("Type not recognized in " + marketData);
        }


        private void CheckConnectionStatus(object state)
        {
            bool isConnected = _socketClient != null && _socketClient.IsRunning;
            if (isConnected)
            {
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTED));
            }
            else
            {
                //throw new Exception("The socket seems to be disconnected.");
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.DISCONNECTED));
            }
        }



        public override object GetUISettings()
        {
            PluginSettingsView view = new PluginSettingsView();
            PluginSettingsViewModel viewModel = new PluginSettingsViewModel(CloseSettingWindow);
            viewModel.ApiSecret = _settings.ApiSecret;
            viewModel.ApiKey = _settings.ApiKey;
            viewModel.ProviderId = _settings.Provider.ProviderID;
            viewModel.ProviderName = _settings.Provider.ProviderName;
            viewModel.Symbols = _settings.Symbols;

            viewModel.UpdateSettingsFromUI = () =>
            {
                _settings.ApiSecret = viewModel.ApiSecret;
                _settings.ApiKey = viewModel.ApiKey;
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = viewModel.ProviderId, ProviderName = viewModel.ProviderName };
                _settings.Symbols = viewModel.Symbols;

                SaveSettings();
                ParseSymbols(string.Join(',', _settings.Symbols.ToArray()));

                //run this because it will allow to reconnect with the new values
                RaiseOnDataReceived(GetProviderModel(eSESSIONSTATUS.CONNECTING));
                Status = ePluginStatus.STARTING;
                isReconnecting = true;
                Task.Run(async () =>
                    await HandleConnectionLost($"{this.Name} is starting (from reloading settings).", null, true));
                isReconnecting = false;
            };
            // Display the view, perhaps in a dialog or a new window.
            view.DataContext = viewModel;
            return view;
        }
        protected override void InitializeDefaultSettings()
        {
            _settings = new PlugInSettings()
            {
                ApiKey = "",
                ApiSecret = "",
                HostName = "https://api.gemini.com/v1/book/",
                WebSocketHostName = "wss://api.gemini.com/v2/marketdata?heartbeat=true",
                WebSocketHostName_UserOrder = "wss://api.gemini.com/v1/order/events?heartbeat=true",
                Provider = new VisualHFT.Model.Provider() { ProviderID = 5, ProviderName = "Gemini" },
                Symbols = new List<string>() { "BTCUSD(BTC/USD)", "ETHUSD(ETH/USD)" }, // Add more symbols as needed
                DepthLevels = 20
            };
            SaveToUserSettings(_settings);
        }
        protected override void LoadSettings()
        {
            _settings = LoadFromUserSettings<PlugInSettings>();
            if (_settings == null)
            {
                InitializeDefaultSettings();
            }
            if (_settings.Provider == null) //To prevent back compability with older setting formats
            {
                _settings.Provider = new VisualHFT.Model.Provider() { ProviderID = 5, ProviderName = "Gemini" };
            }
            ParseSymbols(string.Join(',', _settings.Symbols.ToArray())); //Utilize normalization function
        }
        protected override void SaveSettings()
        {
            SaveToUserSettings(_settings);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!_disposed)
            {
                if (disposing)
                {
                    _socketClient?.Dispose();
                    _heartbeatTimer?.Dispose();
                }
                _disposed = true;
            }
        }

        public void InjectSnapshot(OrderBook snapshotModel, long sequence)
        {
            //1. Call snapshot: creates the local order book, but it won't add any item to it
            //2. Call deltas: it will add the items to the local order book


            var localModel = new InitialResponse(); //transform to local model
            localModel.asks = snapshotModel.Asks.Select(x => new Ask()
            {
                price = x.Price.Value,
                amount = x.Size.Value,
                timestamp = x.LocalTimeStamp.Ticks
            }).ToList();
            localModel.bids = snapshotModel.Bids.Select(x => new Bid()
            {
                price = x.Price.Value,
                amount = x.Size.Value,
                timestamp = x.LocalTimeStamp.Ticks
            }).ToList();
            _settings.DepthLevels = snapshotModel.MaxDepth; //force depth received

            var symbol = snapshotModel.Symbol;

            if (!_localOrderBooks.ContainsKey(symbol))
            {
                _localOrderBooks.Add(symbol, ToOrderBookModel(localModel, symbol));
            }
            else
                _localOrderBooks[symbol] = ToOrderBookModel(localModel, symbol);

            //once called snapshots, we need to update the LOB
            List<List<string>> changes = new List<List<string>>();
            snapshotModel.Asks.ToList().ForEach(x =>
            {
                changes.Add(new List<string>() { "sell", x.Price.Value.ToString(), x.Size.Value.ToString() });
            });
            snapshotModel.Bids.ToList().ForEach(x =>
            {
                changes.Add(new List<string>() { "buy", x.Price.Value.ToString(), x.Size.Value.ToString() });
            });

            UpdateOrderBook(new MarketUpdate()
            {
                Symbol = symbol,
                Type = "l2_updates", //no need here
                Changes = changes,
            }, symbol, DateTime.Now);

            _localOrderBooks[symbol].Sequence = sequence;// Gemini does not provide sequence numbers


            RaiseOnDataReceived(_localOrderBooks[symbol]);

        }
        public void InjectDeltaModel(List<DeltaBookItem> bidDeltaModel, List<DeltaBookItem> askDeltaModel)
        {
            throw new VisualHFT.Commons.Exceptions.ExceptionDeltasNotSupportedByExchange();
        }
        public List<VisualHFT.Model.Order> ExecutePrivateMessageScenario(eTestingPrivateMessageScenario scenario)
        {


            string _file = "";
            if (scenario == eTestingPrivateMessageScenario.SCENARIO_1)
                _file = "PrivateMessages_Scenario1.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_2)
                _file = "PrivateMessages_Scenario2.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_3)
                _file = "PrivateMessages_Scenario3.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_4)
                _file = "PrivateMessages_Scenario4.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_5)
                _file = "PrivateMessages_Scenario5.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_6)
                _file = "PrivateMessages_Scenario6.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_7)
                _file = "PrivateMessages_Scenario7.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_8)
                _file = "PrivateMessages_Scenario8.json";
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_9)
            {
                _file = "PrivateMessages_Scenario9.json";
                throw new Exception("Messages collected for this scenario don't look good.");
            }
            else if (scenario == eTestingPrivateMessageScenario.SCENARIO_10)
            {
                _file = "PrivateMessages_Scenario10.json";
                throw new Exception("Messages were not collected for this scenario.");
            }

            string jsonString = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"Gemini_jsonMessages/{_file}"));
            JsonParser parser = new JsonParser();
            //DESERIALIZE EXCHANGES MODEL
            List<UserOrderData> modelList = new List<UserOrderData>();
            var dataEvents = new List<UserOrderData>();
            var jsonArray = JArray.Parse(jsonString);
            foreach (var jsonObject in jsonArray)
            {
                JArray innerArray = JArray.Parse(jsonObject.ToString());

                string dataJsonString = innerArray[0].ToString();

                UserOrderData _data = parser.Parse<UserOrderData>(dataJsonString);

                if (_data != null)
                    modelList.Add(_data);

            }
            //END UPDATE VISUALHFT CORE


            //UPDATE VISUALHFT CORE & CREATE MODEL TO RETURN
            if (!modelList.Any())
                throw new Exception("No data was found in the json file.");
            foreach (var item in modelList)
            {
                UpdateUserOrderBook(item);
            }
            //END UPDATE VISUALHFT CORE


            //CREATE MODEL TO RETURN (First, identify the order that was sent, then use that one with the updated values)
            var dicOrders = new Dictionary<long, VisualHFT.Model.Order>(); //we need to use dictionary to identify orders (because exchanges orderId is string) 



            foreach (var item in modelList)
            {

                VisualHFT.Model.Order localuserOrder;
                if (!dicOrders.ContainsKey(item.order_id))
                {
                    localuserOrder = new VisualHFT.Model.Order();
                    localuserOrder.ClOrdId = item.client_order_id;
                    localuserOrder.Currency = GetNormalizedSymbol(item.symbol);
                    localuserOrder.OrderID = item.order_id;
                    localuserOrder.ProviderId = _settings!.Provider.ProviderID;
                    localuserOrder.ProviderName = _settings.Provider.ProviderName;
                    localuserOrder.CreationTimeStamp = DateTimeOffset.FromUnixTimeSeconds(item.timestamp).DateTime;
                    localuserOrder.Quantity = item.original_amount;
                    localuserOrder.PricePlaced = item.price;
                    localuserOrder.Symbol = GetNormalizedSymbol(item.symbol);
                    localuserOrder.FilledQuantity = item.executed_amount;
                    localuserOrder.TimeInForce = eORDERTIMEINFORCE.GTC;

                    if (!string.IsNullOrEmpty(item.behavior))
                    {
                        if (item.behavior.ToLower().Equals("immediate-or-cancel"))
                        {
                            localuserOrder.TimeInForce = eORDERTIMEINFORCE.IOC;
                        }
                        else if (item.behavior.ToLower().Equals("fill-or-kill"))
                        {
                            localuserOrder.TimeInForce = eORDERTIMEINFORCE.FOK;
                        }
                        else if (item.behavior.ToLower().Equals("maker-or-cancel"))
                        {
                            localuserOrder.TimeInForce = eORDERTIMEINFORCE.MOK;
                        }
                    }
                    dicOrders.Add(item.order_id, localuserOrder);
                }
                else
                {

                    localuserOrder = dicOrders[item.order_id];
                }
                localuserOrder.OrderType = eORDERTYPE.LIMIT;

                localuserOrder.Quantity = item.original_amount;
                if (item.order_type.ToLower().Equals("limit"))
                {
                    localuserOrder.OrderType = eORDERTYPE.LIMIT;
                }
                else if (item.order_type.ToLower().Equals("exchange limit"))
                {
                    localuserOrder.OrderType = eORDERTYPE.PEGGED;
                }
                else if (item.order_type.ToLower().Equals("market buy"))
                {
                    localuserOrder.OrderType = eORDERTYPE.MARKET;
                }

                if (item.side.ToLower().Equals("sell"))
                {
                    localuserOrder.BestAsk = item.price;
                    localuserOrder.Side = eORDERSIDE.Sell;

                }
                else if (item.side.ToLower().Equals("buy"))
                {
                    localuserOrder.PricePlaced = item.price;
                    localuserOrder.BestBid = item.price;
                    localuserOrder.Side = eORDERSIDE.Buy;

                }

                if (item.type.ToLower().Equals("accepted"))
                {
                    localuserOrder.Status = eORDERSTATUS.NEW;
                }
                else if (item.type.ToLower().Equals("fill"))
                {
                    localuserOrder.FilledQuantity = item.executed_amount;
                    localuserOrder.Status = eORDERSTATUS.PARTIALFILLED;
                }
                else if (item.type.ToLower().Equals("closed"))
                {
                    localuserOrder.Status = eORDERSTATUS.FILLED;
                    if (item.is_cancelled)
                    {
                        localuserOrder.Status = eORDERSTATUS.CANCELED;

                    }
                }
                else if (item.type.ToLower().Equals("rejected"))
                {
                    localuserOrder.Status = eORDERSTATUS.REJECTED;
                }
                else if (item.type.ToLower().Equals("cancelled"))
                {
                    localuserOrder.Status = eORDERSTATUS.CANCELED;
                }
                else if (item.type.ToLower().Equals("cancel_rejected"))
                {
                    localuserOrder.Status = eORDERSTATUS.CANCELED;
                }
                else if (item.type.ToLower().Equals("closed"))
                {

                }
                if (!string.IsNullOrEmpty(item.behavior))
                {
                    if (item.behavior.ToLower().Equals("immediate-or-cancel"))
                    {
                        localuserOrder.TimeInForce = eORDERTIMEINFORCE.IOC;
                    }
                    else if (item.behavior.ToLower().Equals("fill-or-kill"))
                    {
                        localuserOrder.TimeInForce = eORDERTIMEINFORCE.FOK;
                    }
                    else if (item.behavior.ToLower().Equals("maker-or-cancel"))
                    {
                        localuserOrder.TimeInForce = eORDERTIMEINFORCE.MOK;
                    }
                }
                localuserOrder.LastUpdated = DateTime.Now;
                localuserOrder.FilledPercentage = Math.Round((100 / localuserOrder.Quantity) * localuserOrder.FilledQuantity, 2);
                RaiseOnDataReceived(localuserOrder);

            }

            //END CREATE MODEL TO RETURN
            return dicOrders.Values.ToList();

            //ProcessUserOrderData

        }
    }
}