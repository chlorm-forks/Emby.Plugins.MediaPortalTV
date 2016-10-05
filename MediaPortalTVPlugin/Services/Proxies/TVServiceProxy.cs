using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Plugins.MediaPortal.Entities;
using MediaBrowser.Plugins.MediaPortal.Helpers;
using MediaBrowser.Plugins.MediaPortal.Services.Entities;

namespace MediaBrowser.Plugins.MediaPortal.Services.Proxies
{
    /// <summary>
    /// Provides access to the MP tv service functionality
    /// </summary>
    public class TvServiceProxy : ProxyBase
    {
        private readonly StreamingServiceProxy _wssProxy;

        public TvServiceProxy(IHttpClient httpClient, IJsonSerializer serialiser, StreamingServiceProxy wssProxy)
            : base(httpClient, serialiser)
        {
            _wssProxy = wssProxy;
        }

        protected override string EndPointSuffix
        {
            get { return "TVAccessService/json"; }
        }

        #region Get Methods

        public ServiceDescription GetStatusInfo(CancellationToken cancellationToken)
        {
            return GetFromService<ServiceDescription>(cancellationToken, "GetServiceDescription");
        }

        public List<TunerCard> GetTunerCards(CancellationToken cancellationToken)
        {
            return GetFromService<List<TunerCard>>(cancellationToken, "GetCards");
        }

        public List<ActiveTunerCard> GetActiveCards(CancellationToken cancellationToken)
        {
            return GetFromService<List<ActiveTunerCard>>(cancellationToken, "GetActiveCards");
        }

        public List<ChannelGroup> GetChannelGroups(CancellationToken cancellationToken)
        {
            return GetFromService<List<ChannelGroup>>(cancellationToken, "GetGroups").OrderBy(g => g.SortOrder).ToList();
        }
        
        public IEnumerable<ChannelInfo> GetChannels(CancellationToken cancellationToken)
        {
            var builder = new StringBuilder("GetChannelsDetailed");
            if (Configuration.DefaultChannelGroup > 0)
            {
                // This is the only way to get out the channels in the same order that MP displays them.
                builder.AppendFormat("?groupId={0}", Configuration.DefaultChannelGroup);
            }

            var response = GetFromService<List<Channel>>(cancellationToken, builder.ToString());
            IEnumerable<Channel> query = response;

            switch (Configuration.DefaultChannelSortOrder)
            {
                case ChannelSorting.ChannelName:
                    query = query.OrderBy(q => q.Title);
                    break;
                case ChannelSorting.ChannelNumber:
                    query = query.OrderBy(q => q.Id);
                    break;
            }

            return query.Where(c => c.VisibleInGuide).Select(c => new ChannelInfo()
            {
                Id = c.Id.ToString(CultureInfo.InvariantCulture),
                ChannelType = c.IsTv ? ChannelType.TV : ChannelType.Radio,
                Name = c.Title,
                //Number = c.ExternalId,
                Number = " ",
                ImageUrl = _wssProxy.GetChannelLogoUrl(c.Id)
            });
        }

        private Program GetProgram(CancellationToken cancellationToken, String programId)
        {
            return GetFromService<Program>(cancellationToken, "GetProgramDetailedById?programId={0}", programId);
        }

        public IEnumerable<ProgramInfo> GetPrograms(string channelId, DateTime startDateUtc, DateTime endDateUtc,
            CancellationToken cancellationToken)
        {
            int x = 0;
            var response = GetFromService<List<Program>>(
                cancellationToken,
                "GetProgramsDetailedForChannel?channelId={0}&starttime={1}&endtime={2}",
                channelId,
                startDateUtc.ToLocalTime().ToUrlDate(),
                endDateUtc.ToLocalTime().ToUrlDate());

            // Create this once per channel - if created at the class level, then changes to configuration would never be caught
            var genreMapper = new GenreMapper(Plugin.Instance.Configuration);

            var programs = response.Select(p =>
            {
                var program = new ProgramInfo()
                {
                    ChannelId = channelId,
                    StartDate = p.StartTime,
                    EndDate = p.EndTime,
                    EpisodeTitle = p.EpisodeName,
                    Genres = new List<String>(),
                    Id = p.Id.ToString(CultureInfo.InvariantCulture),
                    Name = p.Title,
                    Overview = p.Description,
                    // IsSeries = true,
                    // IsPremiere = false,
                    // IsRepeat = true,
                    // OriginalAirDate = p.OriginalAirDate
                };
                
                if (!String.IsNullOrEmpty(p.EpisodeNum))
                {
                    program.EpisodeNumber = Int32.Parse(p.EpisodeNum);
                }

                if (!String.IsNullOrEmpty(p.SeriesNum))
                {
                    program.SeasonNumber = Int32.Parse(p.SeriesNum);
                }
                
                if (!String.IsNullOrEmpty(p.Genre))
                {
                    program.Genres.Add(p.Genre);
                    // Call Genre Mapper
                    genreMapper.PopulateProgramGenres(program);
                }

                return program;
            });

            return programs;
        }

        public IEnumerable<RecordingInfo> GetRecordings(CancellationToken cancellationToken)
        {
            var response = GetFromService<List<Recording>>(cancellationToken, "GetRecordings");
            var configuration = Plugin.Instance.Configuration;
            if (configuration.EnableDirectAccess && !configuration.RequiresPathSubstitution)
            {
                var recordings = response.Select(r =>
                {
                    var recording = new RecordingInfo()
                    {
                        ChannelId = r.ChannelId.ToString(CultureInfo.InvariantCulture),
                        EndDate = r.EndTime,
                        EpisodeTitle = r.EpisodeName,
                        Genres = new List<String>(),
                        Id = r.Id.ToString(CultureInfo.InvariantCulture),
                        IsSeries = (!String.IsNullOrEmpty(r.EpisodeNum)) ? true : false,
                        Name = r.Title,
                        Overview = r.Description,
                        ProgramId = r.ScheduleId.ToString(CultureInfo.InvariantCulture),
                        StartDate = r.StartTime,
                        //ImageUrl = _wssProxy.GetRecordingImageUrl(r.Id.ToString()),
                        Path = r.FileName,
                    };

                    if (r.IsRecording)
                    {
                        var schedule = GetFromService<Schedule>(cancellationToken, "GetScheduleById?scheduleId={0}", r.ScheduleId);
                        {
                            if (schedule.Series)
                            {
                                recording.SeriesTimerId = schedule.ParentScheduleId.ToString(CultureInfo.InvariantCulture);
                            }
                        } 
                    }
                    
                    if (!String.IsNullOrEmpty(r.Genre))
                    {
                        recording.Genres.Add(r.Genre);
                    }

                    return recording;

                }).ToList();

                return recordings;
            }

            else if (configuration.EnableDirectAccess && configuration.RequiresPathSubstitution)
            {
                var localpath = String.Format("{0}", configuration.LocalFilePath);
                var remotepath = String.Format("{0}", configuration.RemoteFilePath);

                var recordings = response.Select(r =>
                {
                    var recording = new RecordingInfo()
                    {
                        ChannelId = r.ChannelId.ToString(CultureInfo.InvariantCulture),
                        EndDate = r.EndTime,
                        EpisodeTitle = r.EpisodeName,
                        Genres = new List<String>(),
                        Id = r.Id.ToString(CultureInfo.InvariantCulture),
                        IsSeries = (!String.IsNullOrEmpty(r.EpisodeNum)) ? true : false,
                        Name = r.Title,
                        Overview = r.Description,
                        ProgramId = r.ScheduleId.ToString(CultureInfo.InvariantCulture),
                        StartDate = r.StartTime,
                        //ImageUrl = _wssProxy.GetRecordingImageUrl(r.Id.ToString()),
                        Path = r.FileName.Replace(localpath, remotepath),
                    };

                    if (r.IsRecording)
                    {
                        var schedule = GetFromService<Schedule>(cancellationToken, "GetScheduleById?scheduleId={0}", r.ScheduleId);
                        {
                            if (schedule.Series)
                            {
                                recording.SeriesTimerId = schedule.ParentScheduleId.ToString(CultureInfo.InvariantCulture);
                            }
                        }
                    }

                    if (!String.IsNullOrEmpty(r.Genre))
                    {
                        recording.Genres.Add(r.Genre);
                    }

                    return recording;

                }).ToList();

                return recordings;
            }
            
            else
            {
            var recordings = response.Select(r =>
            {
                var recording = new RecordingInfo()
                {
                    ChannelId = r.ChannelId.ToString(CultureInfo.InvariantCulture),
                    EndDate = r.EndTime,
                    EpisodeTitle = r.EpisodeName,
                    Genres = new List<String>(),
                    Id = r.Id.ToString(CultureInfo.InvariantCulture),
                        IsSeries = (!String.IsNullOrEmpty(r.EpisodeNum)) ? true : false,
                    Name = r.Title,
                    Overview = r.Description,
                    ProgramId = r.ScheduleId.ToString(CultureInfo.InvariantCulture),
                    StartDate = r.StartTime,
                    //ImageUrl = _wssProxy.GetRecordingImageUrl(r.Id.ToString(), scheduleDefaults.PreRecordInterval),
                };

                    if (r.IsRecording)
                    {
                        var schedule = GetFromService<Schedule>(cancellationToken, "GetScheduleById?scheduleId={0}", r.ScheduleId);
                        {
                            if (schedule.Series)
                            {
                                recording.SeriesTimerId = schedule.ParentScheduleId.ToString(CultureInfo.InvariantCulture);
                            }
                        }
                    }

                if (!String.IsNullOrEmpty(r.Genre))
                {
                    recording.Genres.Add(r.Genre);
                }

                return recording;

            }).ToList();

            return recordings;
        }
        }

        public RecordingInfo GetRecording(CancellationToken cancellationToken, String id)
        {
            var response = GetFromService<Recording>(cancellationToken, "GetRecordingById?id={0}", id);
            var configuration = Plugin.Instance.Configuration;
            if (configuration.EnableDirectAccess && !configuration.RequiresPathSubstitution)
            {
                var recording = new RecordingInfo()
                {
                    ChannelId = response.ChannelId.ToString(CultureInfo.InvariantCulture),
                    EndDate = response.EndTime,
                    EpisodeTitle = response.EpisodeName,
                    Genres = new List<String>(),
                    Id = response.Id.ToString(CultureInfo.InvariantCulture),
                    IsSeries = (!String.IsNullOrEmpty(response.EpisodeNum)) ? true : false,
                    Name = response.Title,
                    Overview = response.Description,
                    ProgramId = response.ScheduleId.ToString(CultureInfo.InvariantCulture),
                    StartDate = response.StartTime,
                    Path = response.FileName,
                };

                if (response.IsRecording)
                {
                    var schedule = GetFromService<Schedule>(cancellationToken, "GetScheduleById?scheduleId={0}", response.ScheduleId);
                    {
                        if (schedule.Series)
                        {
                            recording.SeriesTimerId = schedule.ParentScheduleId.ToString(CultureInfo.InvariantCulture);
                        }
                    }
                }

                if (!String.IsNullOrEmpty(response.Genre))
                {
                    recording.Genres.Add(response.Genre);
                }

                return recording;
            }
            
            else if (configuration.EnableDirectAccess && configuration.RequiresPathSubstitution)
            {
                var localpath = String.Format("{0}", configuration.LocalFilePath);
                var remotepath = String.Format("{0}", configuration.RemoteFilePath);

            var recording = new RecordingInfo()
            {
                ChannelId = response.ChannelId.ToString(CultureInfo.InvariantCulture),
                EndDate = response.EndTime,
                EpisodeTitle = response.EpisodeName,
                Genres = new List<String>(),
                Id = response.Id.ToString(CultureInfo.InvariantCulture),
                    IsSeries = (!String.IsNullOrEmpty(response.EpisodeNum)) ? true : false,
                Name = response.Title,
                Overview = response.Description,
                ProgramId = response.ScheduleId.ToString(CultureInfo.InvariantCulture),
                StartDate = response.StartTime,
                    Path = response.FileName.Replace(localpath, remotepath),
            };

                if (response.IsRecording)
                {
                    var schedule = GetFromService<Schedule>(cancellationToken, "GetScheduleById?scheduleId={0}", response.ScheduleId);
                    {
                        if (schedule.Series)
                        {
                            recording.SeriesTimerId = schedule.ParentScheduleId.ToString(CultureInfo.InvariantCulture);
                        }
                    }
                }

            if (!String.IsNullOrEmpty(response.Genre))
            {
                recording.Genres.Add(response.Genre);
            }

            return recording;
        }

            else
            {
                var recording = new RecordingInfo()
                {
                    ChannelId = response.ChannelId.ToString(CultureInfo.InvariantCulture),
                    EndDate = response.EndTime,
                    EpisodeTitle = response.EpisodeName,
                    Genres = new List<String>(),
                    Id = response.Id.ToString(CultureInfo.InvariantCulture),
                    IsSeries = (!String.IsNullOrEmpty(response.EpisodeNum)) ? true : false,
                    Name = response.Title,
                    Overview = response.Description,
                    ProgramId = response.ScheduleId.ToString(CultureInfo.InvariantCulture),
                    StartDate = response.StartTime,
                };

                if (response.IsRecording)
                {
                    var schedule = GetFromService<Schedule>(cancellationToken, "GetScheduleById?scheduleId={0}", response.ScheduleId);
                    {
                        if (schedule.Series)
                        {
                            recording.SeriesTimerId = schedule.ParentScheduleId.ToString(CultureInfo.InvariantCulture);
                        }
                    }
                }

                if (!String.IsNullOrEmpty(response.Genre))
                {
                    recording.Genres.Add(response.Genre);
                }

                return recording;
            }

        }

        private Schedule GetSchedule(CancellationToken cancellationToken, String Id)
        {
            return GetFromService<Schedule>(cancellationToken, "GetScheduleById?scheduleId={0}", Id);
        }

        public IEnumerable<SeriesTimerInfo> GetSeriesSchedules(CancellationToken cancellationToken)
        {
            var response = GetFromService<List<Schedule>>(cancellationToken, "GetSchedules");

            var recordings = response.Where(r => r.ScheduleType > 0).Select(r =>
            {
                var seriesTimerInfo = new SeriesTimerInfo()
                {
                    ChannelId = r.ChannelId.ToString(CultureInfo.InvariantCulture),
                    EndDate = r.EndTime,
                    Id = r.Id.ToString(CultureInfo.InvariantCulture),
                    SeriesId = r.Id.ToString(CultureInfo.InvariantCulture),
                    ProgramId = r.Id.ToString(CultureInfo.InvariantCulture),
                    Name = r.Title,
                    IsPostPaddingRequired = (r.PostRecordInterval > 0),
                    IsPrePaddingRequired = (r.PreRecordInterval > 0),
                    PostPaddingSeconds = r.PostRecordInterval * 60,
                    PrePaddingSeconds = r.PreRecordInterval * 60,
                    StartDate = r.StartTime,
                };

                UpdateScheduling(seriesTimerInfo, r);
                
                return seriesTimerInfo;
            });

            return recordings;
        }

        private void UpdateScheduling(SeriesTimerInfo seriesTimerInfo, Schedule schedule)
        {
            var schedulingType = (WebScheduleType)schedule.ScheduleType;

            // Initialise
            seriesTimerInfo.Days = new List<DayOfWeek>();
            seriesTimerInfo.RecordAnyChannel = false;
            seriesTimerInfo.RecordAnyTime = false;
            seriesTimerInfo.RecordNewOnly = false;

            switch (schedulingType)
            {
                case WebScheduleType.EveryTimeOnThisChannel:
                    seriesTimerInfo.RecordAnyTime = true;
                    break;
                case WebScheduleType.EveryTimeOnEveryChannel:
                    seriesTimerInfo.RecordAnyTime = true;
                    seriesTimerInfo.RecordAnyChannel = true;
                    break;
                case WebScheduleType.WeeklyEveryTimeOnThisChannel:
                    seriesTimerInfo.Days.Add(schedule.StartTime.DayOfWeek);
                    seriesTimerInfo.RecordAnyTime = true;
                    break;
                case WebScheduleType.Daily:
                    seriesTimerInfo.Days.AddRange(new[]
                        {
                            DayOfWeek.Monday,
                            DayOfWeek.Tuesday,
                            DayOfWeek.Wednesday,
                            DayOfWeek.Thursday,
                            DayOfWeek.Friday,
                            DayOfWeek.Saturday,
                            DayOfWeek.Sunday,
                        });
                    break;
                case WebScheduleType.WorkingDays:
                        seriesTimerInfo.Days.AddRange(new[]
                        {
                            DayOfWeek.Monday,
                            DayOfWeek.Tuesday,
                            DayOfWeek.Wednesday,
                            DayOfWeek.Thursday,
                            DayOfWeek.Friday,
                        });
                    break;
                case WebScheduleType.Weekends:
                        seriesTimerInfo.Days.AddRange(new[]
                        {
                           DayOfWeek.Saturday,
                           DayOfWeek.Sunday,
                        });
                    break;
                case WebScheduleType.Weekly:
                    seriesTimerInfo.Days.Add(schedule.StartTime.DayOfWeek);
                    break;

                default:
                    throw new InvalidOperationException(String.Format("Should not be processing scheduling for ScheduleType={0}", schedulingType));
            }
        }

        public IEnumerable<TimerInfo> GetSchedules(CancellationToken cancellationToken)
        {
            var response = GetFromService<List<Schedule>>(cancellationToken, "GetSchedules");

            var recordings = response.Where(r => r.ScheduleType == 0).Select(r => new TimerInfo()
            {
                ChannelId = r.ChannelId.ToString(CultureInfo.InvariantCulture),
                EndDate = r.EndTime,
                Id = r.Id.ToString(CultureInfo.InvariantCulture),
                SeriesTimerId = r.ParentScheduleId.ToString(CultureInfo.InvariantCulture),
                ProgramId = r.Id.ToString(CultureInfo.InvariantCulture),
                Name = r.Title,
                IsPostPaddingRequired = (r.PostRecordInterval > 0),
                IsPrePaddingRequired = (r.PreRecordInterval > 0),
                PostPaddingSeconds = r.PostRecordInterval * 60,
                PrePaddingSeconds = r.PreRecordInterval * 60,
                Status = RecordingStatus.New,
                StartDate = r.StartTime,
            }).ToList();

            return recordings;
        }

        #endregion

        #region Streaming Methods

        public String SwitchTVChannelAndStream(CancellationToken cancellationToken, Int32 channelId)
        {
            var userName = String.Empty;
            return GetFromService<String>(cancellationToken, 
                "SwitchTVServerToChannelAndGetStreamingUrl?userName={0}&channelId={1}",
                userName,
                channelId);
        }

        #endregion

        #region Create Methods

        public void CreateSeriesSchedule(CancellationToken cancellationToken, SeriesTimerInfo schedule)
        {
            var programData = GetProgram(cancellationToken, schedule.ProgramId);
            if (programData == null)
            {
                throw ExceptionHelper.CreateArgumentException("schedule.ProgramId", "The program id {0} could not be found", schedule.ProgramId);
            }

            var builder = new StringBuilder("AddScheduleDetailed?");
            builder.AppendFormat("channelid={0}&", programData.ChannelId);
            builder.AppendFormat("title={0}&", programData.Title);
            builder.AppendFormat("starttime={0}&", programData.StartTime.ToLocalTime().ToUrlDate());
            builder.AppendFormat("endtime={0}&", programData.EndTime.ToLocalTime().ToUrlDate());
            builder.AppendFormat("scheduletype={0}&", (Int32)schedule.ToScheduleType());

            if (schedule.IsPrePaddingRequired & schedule.PrePaddingSeconds > 0)
            {
                builder.AppendFormat("preRecordInterval={0}&", TimeSpan.FromSeconds(schedule.PrePaddingSeconds).RoundUpMinutes());
            }

            if (schedule.IsPostPaddingRequired & schedule.PostPaddingSeconds > 0)
            {
                builder.AppendFormat("postRecordInterval={0}&", TimeSpan.FromSeconds(schedule.PostPaddingSeconds).RoundUpMinutes());
            }

            builder.Remove(builder.Length - 1, 1);

            Plugin.Logger.Info("Creating series schedule with StartTime: {0}, EndTime: {1}, ProgramData from MP: {2}",
                schedule.StartDate, schedule.EndDate, builder.ToString());

            var response = GetFromService<WebBoolResult>(cancellationToken, builder.ToString());
            if (!response.Result)
            {
                throw new LiveTvConflictException();
            }
        }

        public void ChangeSeriesSchedule(CancellationToken cancellationToken, SeriesTimerInfo schedule)
        {
            var timerData = GetSchedule(cancellationToken, schedule.Id);
            if (timerData == null)
            {
                throw ExceptionHelper.CreateArgumentException("schedule.Id", "The schedule id {0} could not be found", schedule.Id);
            }

            var builder = new StringBuilder("AddScheduleDetailed?");
            builder.AppendFormat("channelid={0}&", timerData.ChannelId);
            builder.AppendFormat("title={0}&", timerData.Title);
            builder.AppendFormat("starttime={0}&", timerData.StartTime.ToLocalTime().ToUrlDate());
            builder.AppendFormat("endtime={0}&", timerData.EndTime.ToLocalTime().ToUrlDate());
            builder.AppendFormat("scheduletype={0}&", (Int32)schedule.ToScheduleType());

            if (schedule.IsPrePaddingRequired & schedule.PrePaddingSeconds > 0)
            {
                builder.AppendFormat("preRecordInterval={0}&", TimeSpan.FromSeconds(schedule.PrePaddingSeconds).RoundUpMinutes());
            }

            if (schedule.IsPostPaddingRequired & schedule.PostPaddingSeconds > 0)
            {
                builder.AppendFormat("postRecordInterval={0}&", TimeSpan.FromSeconds(schedule.PostPaddingSeconds).RoundUpMinutes());
            }

            builder.Remove(builder.Length - 1, 1);

            Plugin.Logger.Info("Creating series schedule with StartTime: {0}, EndTime: {1}, ProgramData from MP: {2}",
                schedule.StartDate, schedule.EndDate, builder.ToString());

            Plugin.TvProxy.DeleteSchedule(cancellationToken, schedule.Id);

            var response = GetFromService<WebBoolResult>(cancellationToken, builder.ToString());
            if (!response.Result)
            {
                throw new LiveTvConflictException();
            }

        }

        public void CreateSchedule(CancellationToken cancellationToken, TimerInfo timer)
        {
            var programData = GetProgram(cancellationToken, timer.ProgramId);
            if (programData == null)
            {
                throw ExceptionHelper.CreateArgumentException("timer.ProgramId", "The program id {0} could not be found", timer.ProgramId);
            }

            var builder = new StringBuilder("AddScheduleDetailed?");
            builder.AppendFormat("channelid={0}&", programData.ChannelId);
            builder.AppendFormat("title={0}&", programData.Title);
            builder.AppendFormat("starttime={0}&", programData.StartTime.ToLocalTime().ToUrlDate());
            builder.AppendFormat("endtime={0}&", programData.EndTime.ToLocalTime().ToUrlDate());
            builder.AppendFormat("scheduletype={0}&", (Int32)WebScheduleType.Once);

            if (timer.IsPrePaddingRequired & timer.PrePaddingSeconds > 0)
            {
                builder.AppendFormat("preRecordInterval={0}&", timer.PrePaddingSeconds / 60);
            }

            if (timer.IsPostPaddingRequired & timer.PostPaddingSeconds > 0)
            {
                builder.AppendFormat("postRecordInterval={0}&", timer.PostPaddingSeconds / 60);
            }

            builder.Remove(builder.Length - 1, 1);

            Plugin.Logger.Info("Creating schedule with StartTime: {0}, EndTime: {1}, ProgramData from MP: {2}",
                timer.StartDate, timer.EndDate, builder.ToString());

            var response = GetFromService<WebBoolResult>(cancellationToken, builder.ToString());
            if (!response.Result)
            {
                throw new LiveTvConflictException();
            }
        }

        #endregion

        #region Delete Methods

        public void DeleteSchedule(CancellationToken cancellationToken, string scheduleId)
        {
            var response = GetFromService<WebBoolResult>(cancellationToken,
                "DeleteSchedule?scheduleId={0}",
                scheduleId);

            if (!response.Result)
            {
                throw new LiveTvConflictException();
            }
        }

        public void DeleteRecording(CancellationToken cancellationToken, string programId)
        {
            try
            {
                var response = GetFromService<WebBoolResult>(cancellationToken,
                    "DeleteRecording?id={0}",
                    programId);

                if (!response.Result)
                {
                    throw new LiveTvConflictException();
                }
            }
            catch (AggregateException ex)
            {
                if (ex.InnerExceptions.OfType<HttpException>().All(e => e.StatusCode != HttpStatusCode.NotFound))
                {
                    throw;
                }
            }

        }

        #endregion

        #region Other Methods

        public ScheduleDefaults GetScheduleDefaults(CancellationToken cancellationToken)
        {
            Int32 preRecordSecs;
            Int32 postRecordSecs;

            if (!Int32.TryParse(ReadSettingFromDatabase(cancellationToken, "preRecordInterval"), out preRecordSecs))
            {
                Plugin.Logger.Warn("Unable to read the setting 'preRecordInterval' from MP");
            }

            if (!Int32.TryParse(ReadSettingFromDatabase(cancellationToken, "postRecordInterval"), out postRecordSecs))
            {
                Plugin.Logger.Warn("Unable to read the setting 'postRecordInterval' from MP");
            }

            return new ScheduleDefaults()
            {
                PreRecordInterval = TimeSpan.FromMinutes(preRecordSecs),
                PostRecordInterval = TimeSpan.FromMinutes(postRecordSecs),
            };
        }

        public String ReadSettingFromDatabase(CancellationToken cancellationToken, String name)
        {
            return GetFromService<WebStringResult>(cancellationToken, "ReadSettingFromDatabase?tagName={0}", name).Result;
        }

        #endregion
    }
}