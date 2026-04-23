using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace NeftaCustomAdapter
{
    public abstract class AdLogic
    {
        protected enum State
        {
            Idle,
            LoadingWithInsights,
            Loading,
            Ready,
            Shown
        }
                
        protected class Track
        {
            public readonly string AdUnitId;
            public State State;
            public AdInsight Insight;
            public MaxSdkBase.AdInfo AdInfo;
            public bool IsAdLoadCallbackAvailable;
            public CancellationTokenSource ResponseCancellationToken;

            public Track(string adUnitId)
            {
                AdUnitId = adUnitId;
            }

            public void  Reset()
            {
                IsAdLoadCallbackAvailable = false;
                Insight = null;
                State = State.Idle;
                AdInfo = null;
            }
        }
        
        protected abstract string LogTag { get; }
        protected abstract NeftaAdapterEvents.AdType AdType { get; }
        protected abstract int InsightType { get; }
        protected abstract void LoadInternal(string adUnitId, string bidFloor);
        protected abstract bool TryShow(Track adRequest);
        
        protected Track _trackA;
        protected Track _trackB;
        protected bool _isFirstResponseReceived;
        protected bool _isAdRequested;
        
        public Action<string, MaxSdkBase.AdInfo> OnAdLoadedEvent;
        public Action<string, MaxSdkBase.ErrorInfo> OnAdLoadFailedEvent;
        public Action<string, MaxSdkBase.AdInfo> OnAdDisplayedEvent;
        public Action<string, MaxSdkBase.ErrorInfo, MaxSdkBase.AdInfo> OnAdDisplayFailedEvent;
        public Action<string, MaxSdkBase.AdInfo> OnAdClickedEvent;
        public Action<string, MaxSdkBase.AdInfo> OnAdRevenuePaidEvent;
        public Action<string, string, MaxSdkBase.AdInfo> OnAdReviewCreativeIdGeneratedEvent;
        public Action<string, MaxSdkBase.AdInfo> OnAdHiddenEvent;
        
        public bool IsDualTrackInitialized { get; protected set; }

        public virtual void InitializeDualTrack(string adUnitIdA, string adUnitIdB)
        {
            _trackA = new Track(adUnitIdA);
            _trackB = new Track(adUnitIdB);
            NeftaSdk.Initialize();

            IsDualTrackInitialized = true;
        }

        public virtual void OnNewSession()
        {
            _trackA.Reset();
            _trackB.Reset();
            
            _isFirstResponseReceived = false;
            LoadTracks();
        }

        /// <summary>
        /// When using wrapper OnAdLoadEvent callback is forwarded only after Load request.
        /// Since Wrapper might already have an ad ready because of dual track loading.
        /// </summary>
        public void LoadOrTriggerAlreadyLoaded()
        {
            if (_trackA.State == State.Ready && _trackA.IsAdLoadCallbackAvailable)
            {
                _trackA.IsAdLoadCallbackAvailable = false;
                if (OnAdLoadedEvent != null)
                {
                    OnAdLoadedEvent(_trackA.AdUnitId, _trackA.AdInfo);
                    return;
                }
            }
            if (_trackB.State == State.Ready && _trackB.IsAdLoadCallbackAvailable)
            {
                _trackB.IsAdLoadCallbackAvailable = false;
                if (OnAdLoadedEvent != null)
                {
                    OnAdLoadedEvent(_trackB.AdUnitId, _trackB.AdInfo);
                    return;
                }
            }
            
            _isAdRequested = true;
            LoadTracks();
        }
        
        protected void LoadTracks()
        {
            TrackLoad(_trackA, _trackB.State);
            TrackLoad(_trackB, _trackA.State);
        }
        
        private void TrackLoad(Track track, State otherState)
        {
            if (track.State == State.Idle)
            {
                if (otherState == State.LoadingWithInsights || otherState == State.Shown)
                {
                    if (_isFirstResponseReceived)
                    {
                        LoadDefault(track);
                    }
                }
                else
                {
                    GetInsightsAndLoad(track); 
                }
            }
        }
        
        protected void GetInsightsAndLoad(Track track)
        {
            track.State = State.LoadingWithInsights;
            
            NeftaAdapterEvents.GetInsights(InsightType, track.Insight, (Insights insights) =>
            {
                Log($"Load with Insights: {insights}");
                if (insights.Insight != null)
                {
                    track.Insight = insights.Insight;
                    NeftaAdapterEvents.OnExternalMediationRequest(AdType, track.AdUnitId, track.Insight);
                    var bidFloor = track.Insight._floorPrice.ToString(CultureInfo.InvariantCulture);
                    Log($"Loading {track.AdUnitId} as Optimized with floor: {bidFloor}");
                    
                    LoadInternal(track.AdUnitId, bidFloor);

                    if (NeftaAdapterEvents.NoResponseRetryInMs > 0)
                    {
                        RetryOnNoResponse(track);
                    }
                }
                else
                {
                    RestartAfterFailedLoad(track);
                }
            });
        }

        private async void RetryOnNoResponse(Track track)
        {
            try
            {
                track.ResponseCancellationToken = new CancellationTokenSource();
                await Task.Delay(NeftaAdapterEvents.NoResponseRetryInMs, track.ResponseCancellationToken.Token);
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    return;
                }
#endif
                Log($"Retrying load on {track.AdUnitId}");
                track.State = State.Idle;
                LoadTracks();
            }
            catch (TaskCanceledException)
            {
            }
        }
        
        protected void LoadDefault(Track track)
        {
            track.State = State.Loading;
            NeftaAdapterEvents.OnExternalMediationRequest(AdType, track.AdUnitId);
            Log($"Loading {track.AdUnitId} as Default");
            
            LoadInternal(track.AdUnitId, null);
        }
        
        private void RestartAfterFailedLoad(Track track)
        {
            _ = RetryLoadWithDelay(track);
            
            _isFirstResponseReceived = true;
            LoadTracks();
        }
        
        private async Task RetryLoadWithDelay(Track track)
        {
            var delay = NeftaAdapterEvents.GetRetryDelayInSeconds(track.Insight);
            await Task.Delay((int)(delay * 1000));
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                return;
            }
#endif
            track.State = State.Idle;
            LoadTracks();
        }
        
        public bool IsAdReady()
        {
            return _trackA.State == State.Ready || _trackB.State == State.Ready;
        }
        
        public void ShowAd()
        {
            var isShown = false;
            if (_trackA.State == State.Ready)
            {
                if (_trackB.State == State.Ready && _trackB.AdInfo.Revenue > _trackA.AdInfo.Revenue)
                {
                    isShown = TryShow(_trackB);
                }
                if (!isShown)
                {
                    isShown = TryShow(_trackA);
                }
            }
            if (!isShown && _trackB.State == State.Ready)
            {
                TryShow(_trackB);
            }
        }
        
        protected void OnAdFailedCallback(string adUnitId, MaxSdkBase.ErrorInfo errorInfo)
        {
            if (!NeftaSdk.IsNeftaDisabled)
            {
                NeftaAdapterEvents.OnExternalMediationRequestFailed(adUnitId, errorInfo);
            }
            if (NeftaSdk.Passthrough)
            {
                if (OnAdLoadFailedEvent != null)
                {
                    OnAdLoadFailedEvent(adUnitId, errorInfo);
                }
                return;
            }
            
            Log($"Load Failed {adUnitId}: {errorInfo}");
            
            var track = adUnitId == _trackA.AdUnitId ? _trackA : _trackB;
            if (track.ResponseCancellationToken != null)
            {
                track.ResponseCancellationToken.Cancel();
            }
            RestartAfterFailedLoad(track);
        }
        
        protected void OnAdLoadedCallback(string adUnitId, MaxSdkBase.AdInfo adInfo)
        {
            if (!NeftaSdk.IsNeftaDisabled)
            {
                NeftaAdapterEvents.OnExternalMediationRequestLoaded(adInfo);
            }
            if (NeftaSdk.Passthrough)
            {
                if (OnAdLoadedEvent != null)
                {
                    OnAdLoadedEvent(adUnitId, adInfo);
                }
                return;
            }
            
            Log($"Loaded {adUnitId} at: {adInfo.Revenue}");
            
            var track = adUnitId == _trackA.AdUnitId ? _trackA : _trackB;
            if (track.ResponseCancellationToken != null)
            {
                track.ResponseCancellationToken.Cancel();
            }
            track.Insight = null;
            track.AdInfo = adInfo;
            track.State = State.Ready;
            _isFirstResponseReceived = true;
            
            if (_isAdRequested)
            {
                _isAdRequested = false;
                
                if (OnAdLoadedEvent != null)
                {
                    OnAdLoadedEvent(adUnitId, adInfo);   
                }
            }
            else
            {
                track.IsAdLoadCallbackAvailable = true;
            }
            
            LoadTracks();
        }
        
        protected void OnAdDisplayFailedCallback(string adUnitId, MaxSdkBase.ErrorInfo errorInfo, MaxSdkBase.AdInfo adInfo)
        {
            if (NeftaSdk.Passthrough)
            {
                if (OnAdDisplayFailedEvent != null)
                {
                    OnAdDisplayFailedEvent(adUnitId, errorInfo, adInfo);
                }
                return;
            }

            Log($"OnAdDisplayFailedEvent {adUnitId}");
            
            var track = adUnitId == _trackA.AdUnitId ? _trackA : _trackB;
            track.State = State.Idle;
            
            if (OnAdDisplayFailedEvent != null)
            {
                OnAdDisplayFailedEvent(adUnitId, errorInfo, adInfo);
            }
            
            LoadTracks();
        }
        
        protected void OnAdDisplayedCallback(string adUnitId, MaxSdkBase.AdInfo adInfo)
        {
            if (OnAdDisplayedEvent != null)
            {
                OnAdDisplayedEvent(adUnitId, adInfo);
            }
        }
        
        protected void OnAdRevenuePaidCallback(string adUnitId, MaxSdkBase.AdInfo adInfo)
        {
            if (!NeftaSdk.IsNeftaDisabled)
            {
                NeftaAdapterEvents.OnExternalMediationImpression(adUnitId, adInfo);
            }

            if (OnAdRevenuePaidEvent != null)
            {
                OnAdRevenuePaidEvent(adUnitId, adInfo);
            }
        }
        
        protected void OnAdClickedCallback(string adUnitId, MaxSdkBase.AdInfo adInfo)
        {
            if (!NeftaSdk.IsNeftaDisabled)
            {
                NeftaAdapterEvents.OnExternalMediationClick(adUnitId, adInfo);
            }

            if (OnAdClickedEvent != null)
            {
                OnAdClickedEvent(adUnitId, adInfo);
            }
        }
        
        protected void OnAdHiddenCallback(string adUnitId, MaxSdkBase.AdInfo adInfo)
        {
            if (NeftaSdk.Passthrough)
            {
                if (OnAdHiddenEvent != null)
                {
                    OnAdHiddenEvent(adUnitId, adInfo);
                }
                return;
            }

            Log($"OnAdHiddenEvent {adUnitId}");
            
            var track = adUnitId == _trackA.AdUnitId ? _trackA : _trackB;
            track.State = State.Idle;
            
            if (OnAdHiddenEvent != null)
            {
                OnAdHiddenEvent(adUnitId, adInfo);
            }
            
            LoadTracks();
        }
        
        protected void Log(string log)
        {
            if (NeftaAdapterEvents.IsLoggingEnabled)
            {
                Debug.Log($"{LogTag}: {log}");
            }
        }
    }
}