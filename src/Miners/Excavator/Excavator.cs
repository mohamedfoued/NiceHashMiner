﻿using Newtonsoft.Json;
using NHM.Common;
using NHM.Common.Device;
using NHM.Common.Enums;
using NHM.MinerPlugin;
using NHM.MinerPluginToolkitV1;
using NHM.MinerPluginToolkitV1.CCMinerCommon;
using NHM.MinerPluginToolkitV1.Interfaces;
using NHM.MinerPluginToolkitV1.Configs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Excavator
{
    public class Excavator : MinerBase, IAfterStartMining
    {
        private int _apiPort;

        public Excavator(string uuid) : base(uuid)
        {}

        protected virtual string AlgorithmName(AlgorithmType algorithmType) => PluginSupportedAlgorithms.AlgorithmName(algorithmType);

        private object _lock2 = new object();
        private ApiData _lastApiData = null;

        public override async Task<ApiData> GetMinerStatsDataAsync()
        {
            var ad = new ApiData();
            lock (_lock2)
            {
                if (_lastApiData != null)
                {
                    ad.PowerUsageTotal = _lastApiData.PowerUsageTotal;
                    ad.AlgorithmSpeedsPerDevice = _lastApiData.AlgorithmSpeedsPerDevice;
                    ad.PowerUsagePerDevice = _lastApiData.PowerUsagePerDevice;
                    ad.ApiResponse = _lastApiData.ApiResponse;
                }
            }
            await Task.CompletedTask; // stub just to have same interface 
            return ad;
        }

        private async Task<ApiData> GetMinerStatsDataAsyncPrivate()
        {
            var ad = new ApiData();
            try
            {
                const string speeds = @"{""id"":123456789,""method"":""worker.list"",""params"":[]}" + "\r\n";
                var response = await ApiDataHelpers.GetApiDataAsync(_apiPort, speeds, _logGroup);
                ad.ApiResponse = response;
                var summary = JsonConvert.DeserializeObject<JsonApiResponse>(response);
                var gpus = _miningPairs.Select(pair => pair.Device.UUID);
                var perDeviceSpeedInfo = new Dictionary<string, IReadOnlyList<(AlgorithmType type, double speed)>>();
                var perDevicePowerInfo = new Dictionary<string, int>();
                var totalSpeed = 0d;
                //var totalSpeed2 = 0d;
                //var totalPowerUsage = 0;
                foreach (var gpu in gpus)
                {
                    var speed = summary.workers.Where(w => w.device_uuid == gpu).SelectMany(w => w.algorithms.Select(a => a.speed)).Sum();
                    totalSpeed += speed;
                    perDeviceSpeedInfo.Add(gpu, new List<(AlgorithmType type, double speed)>() { (_algorithmType, speed) });
                }
                ad.PowerUsageTotal = 0;
                ad.AlgorithmSpeedsPerDevice = perDeviceSpeedInfo;
                ad.PowerUsagePerDevice = perDevicePowerInfo;
                await Task.CompletedTask;
            }
            catch (Exception e)
            {
                Logger.Error("EXCAVATOR-API_ERR", e.ToString());
            }
            return ad;
        }

        protected override void Init() {}

        private static string GetServiceLocation(string miningLocation)
        {
            if (BuildOptions.BUILD_TAG == BuildTag.TESTNET) return $"nhmp-ssl-test.{miningLocation}.nicehash.com:443";
            if (BuildOptions.BUILD_TAG == BuildTag.TESTNETDEV) return $"stratum-dev.{miningLocation}.nicehash.com:443";
            //BuildTag.PRODUCTION
            return $"nhmp-ssl.{miningLocation}.nicehash.com:443";
        }

        private static string CmdJSONString(string miningLocation, string username, params string[] uuids)
        {
            const string DEVICE = @"		{""id"":3,""method"":""worker.add"",""params"":[""daggerhashimoto"",""_DEV_ID_""]}";
            const string TEMPLATE = @"
[
	{""time"":0,""commands"":[
		{""id"":1,""method"":""subscribe"",""params"":[""_MINING_SERVICE_LOCATION_"",""_PUT_YOUR_BTC_HERE_""]}
	]},
	{""time"":1,""commands"":[
        {""id"":1,""method"":""algorithm.add"",""params"":[""daggerhashimoto""]}
    ]},
	{""time"":2,""commands"":[
_DEVICES_
	]}
]";
            var devices = string.Join(",\n", uuids.Select(uuid => DEVICE.Replace("_DEV_ID_", uuid)));
            var miningServiceLocation = GetServiceLocation(miningLocation);
            return TEMPLATE
                .Replace("_MINING_SERVICE_LOCATION_", miningServiceLocation)
                .Replace("_PUT_YOUR_BTC_HERE_", username)
                .Replace("_DEVICES_", devices);
        }

        protected override string MiningCreateCommandLine()
        {
            // API port function might be blocking
            _apiPort = GetAvaliablePort();
            var uuids = _miningPairs.Select(p => p.Device).Cast<CUDADevice>().Select(gpu => gpu.UUID);
            var ids = _miningPairs.Select(p => p.Device).Cast<CUDADevice>().Select(gpu => gpu.PCIeBusID);
            //var algo = AlgorithmName(_algorithmType);
            // "--algo {algo} --url={urlWithPort} --user {_username} 
            var (_, cwd) = GetBinAndCwdPaths();
            var fileName = $"cmd_{string.Join("_", ids)}.json";
            //Int32 unixTimestamp = (Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
            //var logName = $"log_{string.Join("_", ids)}_{unixTimestamp}.log";
            var miningLocation = _miningLocation != "eu" ? "usa" : "eu"; // until nhm with obsolete versions is released
            File.WriteAllText(Path.Combine(cwd, fileName), CmdJSONString(miningLocation, _username, uuids.ToArray()));
            var commandLine = $"-p {_apiPort} -c {fileName} -m -qx";
            return commandLine;
        }

        void IAfterStartMining.AfterStartMining()
        {
            var ct = new CancellationTokenSource();
            _miningProcess.Exited += (s, e) =>
            {
                try
                {
                    ct?.Cancel();
                }
                catch
                { }
            };
            _ = MinerSpeedsLoop(ct);
        }

        private async Task MinerSpeedsLoop(CancellationTokenSource ct)
        {
            Logger.Info("EXCAVATOR-MinerSpeedsLoop", $"STARTING");
            try
            {
                var workers = string.Join(",", _miningPairs.Select((_, i) => $@"""{i}"""));
                var workersReset = @"{""id"":1,""method"":""workers.reset"",""params"":[__WORKERS__]}".Replace("__WORKERS__", workers);
                Func<bool> isActive = () => !ct.Token.IsCancellationRequested;
                while (isActive())
                {
                    try
                    {
                        _ = await ApiDataHelpers.GetApiDataAsync(_apiPort, workersReset + "\r\n", _logGroup);
                        if (isActive()) await ExcavatorTaskHelpers.TryDelay(TimeSpan.FromSeconds(30), ct.Token);
                        // get speeds
                        var ad = await GetMinerStatsDataAsyncPrivate();
                        lock (_lock2)
                        {
                            _lastApiData = ad;
                        }
                        // speed print and reset
                        _ = await ApiDataHelpers.GetApiDataAsync(_apiPort, @"{""id"":1,""method"":""worker.print.efficiencies"",""params"":[]}" + "\r\n", _logGroup);
                    }
                    catch (TaskCanceledException e)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                    }
                }
                Logger.Info("EXCAVATOR-MinerSpeedsLoop", $"EXIT WHILE");
            }
            catch (TaskCanceledException e)
            {
            }
            catch (Exception ex)
            {
                Logger.Error("EXCAVATOR-API_LOOP", $"error {ex}");
            }
            finally
            {
                ct.Dispose();
            }
        }

        public override async Task<BenchmarkResult> StartBenchmark(CancellationToken stop, BenchmarkPerformanceType benchmarkType = BenchmarkPerformanceType.Standard)
        {
            using (var tickCancelSource = new CancellationTokenSource())
            {
                var workers = string.Join(",", _miningPairs.Select((_, i) => $@"""{i}"""));
                var workersReset = @"{""id"":1,""method"":"" workers.reset"",""params"":[__WORKERS__]}".Replace("__WORKERS__", workers);

                // determine benchmark time 
                // settup times
                var benchmarkTime = MinerBenchmarkTimeSettings.ParseBenchmarkTime(new List<int> { 20, 40, 60 }, MinerBenchmarkTimeSettings, _miningPairs, benchmarkType); // in seconds
                var maxTicks = MinerBenchmarkTimeSettings.ParseBenchmarkTicks(new List<int> { 1, 3, 9 }, MinerBenchmarkTimeSettings, _miningPairs, benchmarkType);
                var maxTicksEnabled = MinerBenchmarkTimeSettings.MaxTicksEnabled;

                //// use demo user and disable the watchdog
                var commandLine = MiningCreateCommandLine();
                var (binPath, binCwd) = GetBinAndCwdPaths();
                Logger.Info(_logGroup, $"Benchmarking started with command: {commandLine}");
                Logger.Info(_logGroup, $"Benchmarking settings: time={benchmarkTime} ticks={maxTicks} ticksEnabled={maxTicksEnabled}");
                var bp = new BenchmarkProcess(binPath, binCwd, commandLine, GetEnvironmentVariables());
                // disable line readings and read speeds from API
                bp.CheckData = null;

                var benchmarkTimeout = TimeSpan.FromSeconds(benchmarkTime + 5);
                var benchmarkWait = TimeSpan.FromMilliseconds(500);
                var t = MinerToolkit.WaitBenchmarkResult(bp, benchmarkTimeout, benchmarkWait, stop, tickCancelSource.Token);


                var stoppedAfterTicks = false;
                var validTicks = 0;
                var ticks = benchmarkTime / 10; // on each 10 seconds tick
                var result = new BenchmarkResult();
                var benchmarkApiData = new List<ApiData>();
                for (var tick = 0; tick < ticks; tick++)
                {
                    if (t.IsCompleted || t.IsCanceled || stop.IsCancellationRequested) break;
                    _ = await ApiDataHelpers.GetApiDataAsync(_apiPort, workersReset + "\r\n", _logGroup);
                    await ExcavatorTaskHelpers.TryDelay(TimeSpan.FromSeconds(10), stop);
                    if (t.IsCompleted || t.IsCanceled || stop.IsCancellationRequested) break;

                    // get speeds
                    var ad = await GetMinerStatsDataAsyncPrivate();
                    var adTotal = ad.AlgorithmSpeedsTotal();
                    var isTickValid = adTotal.Count > 0 && adTotal.All(pair => pair.speed > 0);
                    benchmarkApiData.Add(ad);
                    if (isTickValid) ++validTicks;
                    if (maxTicksEnabled && validTicks >= maxTicks)
                    {
                        stoppedAfterTicks = true;
                        break;
                    }
                }
                // await benchmark task
                if (stoppedAfterTicks)
                {
                    try
                    {
                        tickCancelSource.Cancel();
                    }
                    catch
                    { }
                }
                await t;
                if (stop.IsCancellationRequested)
                {
                    return t.Result;
                }

                // calc speeds
                // TODO calc std deviaton to reduce invalid benches
                try
                {
                    var nonZeroSpeeds = benchmarkApiData.Where(ad => ad.AlgorithmSpeedsTotal().Count > 0 && ad.AlgorithmSpeedsTotal().All(pair => pair.speed > 0))
                                                        .Select(ad => (ad, ad.AlgorithmSpeedsTotal().Count)).ToList();
                    var speedsFromTotals = new List<(AlgorithmType type, double speed)>();
                    if (nonZeroSpeeds.Count > 0)
                    {
                        var maxAlgoPiarsCount = nonZeroSpeeds.Select(adCount => adCount.Count).Max();
                        var sameCountApiDatas = nonZeroSpeeds.Where(adCount => adCount.Count == maxAlgoPiarsCount).Select(adCount => adCount.ad).ToList();
                        var firstPair = sameCountApiDatas.FirstOrDefault();
                        var speedSums = firstPair.AlgorithmSpeedsTotal().Select(pair => new KeyValuePair<AlgorithmType, double>(pair.type, 0.0)).ToDictionary(x => x.Key, x => x.Value);
                        // sum 
                        foreach (var ad in sameCountApiDatas)
                        {
                            foreach (var pair in ad.AlgorithmSpeedsTotal())
                            {
                                speedSums[pair.type] += pair.speed;
                            }
                        }
                        // average
                        foreach (var algoId in speedSums.Keys.ToArray())
                        {
                            speedSums[algoId] /= sameCountApiDatas.Count;
                        }
                        result = new BenchmarkResult
                        {
                            AlgorithmTypeSpeeds = firstPair.AlgorithmSpeedsTotal().Select(pair => (pair.type, speedSums[pair.type])).ToList(),
                            Success = true
                        };
                    }
                }
                catch (Exception e)
                {
                    Logger.Warn(_logGroup, $"benchmarking AlgorithmSpeedsTotal error {e.Message}");
                }

                // return API result
                return result;
            }
        }
    }
}
