using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Http.REST.Attributes;
using NzbDrone.SignalR;
using Readarr.Http;
using Readarr.Http.Extensions;
using Readarr.Http.REST;

namespace Readarr.Api.V1.Queue
{
    [V1ApiController]
    public class QueueController : RestControllerWithSignalR<QueueResource, NzbDrone.Core.Queue.Queue>,
                               IHandle<QueueUpdatedEvent>, IHandle<PendingReleasesUpdatedEvent>
    {
        private readonly IQueueService _queueService;
        private readonly IPendingReleaseService _pendingReleaseService;

        private readonly QualityModelComparer _qualityComparer;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly IIgnoredDownloadService _ignoredDownloadService;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IBlocklistService _blocklistService;

        public QueueController(IBroadcastSignalRMessage broadcastSignalRMessage,
                           IQueueService queueService,
                           IPendingReleaseService pendingReleaseService,
                           QualityProfileService qualityProfileService,
                           ITrackedDownloadService trackedDownloadService,
                           IFailedDownloadService failedDownloadService,
                           IIgnoredDownloadService ignoredDownloadService,
                           IProvideDownloadClient downloadClientProvider,
                           IBlocklistService blocklistService)
            : base(broadcastSignalRMessage)
        {
            _queueService = queueService;
            _pendingReleaseService = pendingReleaseService;
            _trackedDownloadService = trackedDownloadService;
            _failedDownloadService = failedDownloadService;
            _ignoredDownloadService = ignoredDownloadService;
            _downloadClientProvider = downloadClientProvider;
            _blocklistService = blocklistService;

            _qualityComparer = new QualityModelComparer(qualityProfileService.GetDefaultProfile(string.Empty));
        }

        protected override QueueResource GetResourceById(int id)
        {
            throw new NotImplementedException();
        }

        [RestDeleteById]
        public void RemoveAction(int id, bool removeFromClient = true, bool blocklist = false, bool skipReDownload = false)
        {
            var trackedDownload = Remove(id, removeFromClient, blocklist, skipReDownload);

            if (trackedDownload != null)
            {
                _trackedDownloadService.StopTracking(trackedDownload.DownloadItem.DownloadId);
            }
        }

        [HttpDelete("bulk")]
        public object RemoveMany([FromBody] QueueBulkResource resource, [FromQuery] bool removeFromClient = true, [FromQuery] bool blocklist = false, [FromQuery] bool skipReDownload = false)
        {
            var trackedDownloadIds = new List<string>();

            foreach (var id in resource.Ids)
            {
                var trackedDownload = Remove(id, removeFromClient, blocklist, skipReDownload);

                if (trackedDownload != null)
                {
                    trackedDownloadIds.Add(trackedDownload.DownloadItem.DownloadId);
                }
            }

            _trackedDownloadService.StopTracking(trackedDownloadIds);

            return new { };
        }

        [HttpGet]
        public PagingResource<QueueResource> GetQueue(bool includeUnknownAuthorItems = false, bool includeAuthor = false, bool includeBook = false)
        {
            var pagingResource = Request.ReadPagingResourceFromRequest<QueueResource>();
            var pagingSpec = pagingResource.MapToPagingSpec<QueueResource, NzbDrone.Core.Queue.Queue>("timeleft", SortDirection.Ascending);

            return pagingSpec.ApplyToPage((spec) => GetQueue(spec, includeUnknownAuthorItems), (q) => MapToResource(q, includeAuthor, includeBook));
        }

        private PagingSpec<NzbDrone.Core.Queue.Queue> GetQueue(PagingSpec<NzbDrone.Core.Queue.Queue> pagingSpec, bool includeUnknownAuthorItems)
        {
            var ascending = pagingSpec.SortDirection == SortDirection.Ascending;
            var orderByFunc = GetOrderByFunc(pagingSpec);

            var queue = _queueService.GetQueue();
            var filteredQueue = includeUnknownAuthorItems ? queue : queue.Where(q => q.Author != null);
            var pending = _pendingReleaseService.GetPendingQueue();
            var fullQueue = filteredQueue.Concat(pending).ToList();
            IOrderedEnumerable<NzbDrone.Core.Queue.Queue> ordered;

            if (pagingSpec.SortKey == "timeleft")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.Timeleft, new TimeleftComparer())
                    : fullQueue.OrderByDescending(q => q.Timeleft, new TimeleftComparer());
            }
            else if (pagingSpec.SortKey == "estimatedCompletionTime")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.EstimatedCompletionTime, new EstimatedCompletionTimeComparer())
                    : fullQueue.OrderByDescending(q => q.EstimatedCompletionTime,
                        new EstimatedCompletionTimeComparer());
            }
            else if (pagingSpec.SortKey == "protocol")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.Protocol)
                    : fullQueue.OrderByDescending(q => q.Protocol);
            }
            else if (pagingSpec.SortKey == "indexer")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.Indexer, StringComparer.InvariantCultureIgnoreCase)
                    : fullQueue.OrderByDescending(q => q.Indexer, StringComparer.InvariantCultureIgnoreCase);
            }
            else if (pagingSpec.SortKey == "downloadClient")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.DownloadClient, StringComparer.InvariantCultureIgnoreCase)
                    : fullQueue.OrderByDescending(q => q.DownloadClient, StringComparer.InvariantCultureIgnoreCase);
            }
            else if (pagingSpec.SortKey == "quality")
            {
                ordered = ascending
                    ? fullQueue.OrderBy(q => q.Quality, _qualityComparer)
                    : fullQueue.OrderByDescending(q => q.Quality, _qualityComparer);
            }
            else
            {
                ordered = ascending ? fullQueue.OrderBy(orderByFunc) : fullQueue.OrderByDescending(orderByFunc);
            }

            ordered = ordered.ThenByDescending(q => q.Size == 0 ? 0 : 100 - (q.Sizeleft / q.Size * 100));

            pagingSpec.Records = ordered.Skip((pagingSpec.Page - 1) * pagingSpec.PageSize).Take(pagingSpec.PageSize).ToList();
            pagingSpec.TotalRecords = fullQueue.Count;

            if (pagingSpec.Records.Empty() && pagingSpec.Page > 1)
            {
                pagingSpec.Page = (int)Math.Max(Math.Ceiling((decimal)(pagingSpec.TotalRecords / pagingSpec.PageSize)), 1);
                pagingSpec.Records = ordered.Skip((pagingSpec.Page - 1) * pagingSpec.PageSize).Take(pagingSpec.PageSize).ToList();
            }

            return pagingSpec;
        }

        private Func<NzbDrone.Core.Queue.Queue, object> GetOrderByFunc(PagingSpec<NzbDrone.Core.Queue.Queue> pagingSpec)
        {
            switch (pagingSpec.SortKey)
            {
                case "status":
                    return q => q.Status;
                case "authors.sortName":
                    return q => q.Author?.Metadata.Value.SortName ?? string.Empty;
                case "authors.sortNameLastFirst":
                    return q => q.Author?.Metadata.Value.SortNameLastFirst ?? string.Empty;
                case "title":
                    return q => q.Title;
                case "book":
                    return q => q.Book;
                case "book.title":
                    return q => q.Book?.Title ?? string.Empty;
                case "book.releaseDate":
                    return q => q.Book?.ReleaseDate ?? DateTime.MinValue;
                case "quality":
                    return q => q.Quality;
                case "progress":
                    // Avoid exploding if a download's size is 0
                    return q => 100 - (q.Sizeleft / Math.Max(q.Size * 100, 1));
                default:
                    return q => q.Timeleft;
            }
        }

        private TrackedDownload Remove(int id, bool removeFromClient, bool blocklist, bool skipReDownload)
        {
            var pendingRelease = _pendingReleaseService.FindPendingQueueItem(id);

            if (pendingRelease != null)
            {
                _blocklistService.Block(pendingRelease.RemoteBook, "Pending book manually blocklisted");
                _pendingReleaseService.RemovePendingQueueItems(pendingRelease.Id);

                return null;
            }

            var trackedDownload = GetTrackedDownload(id);

            if (trackedDownload == null)
            {
                throw new NotFoundException();
            }

            if (removeFromClient)
            {
                var downloadClient = _downloadClientProvider.Get(trackedDownload.DownloadClient);

                if (downloadClient == null)
                {
                    throw new BadRequestException();
                }

                downloadClient.RemoveItem(trackedDownload.DownloadItem, true);
            }

            if (blocklist)
            {
                _failedDownloadService.MarkAsFailed(trackedDownload.DownloadItem.DownloadId, skipReDownload);
            }

            if (!removeFromClient && !blocklist)
            {
                if (!_ignoredDownloadService.IgnoreDownload(trackedDownload))
                {
                    return null;
                }
            }

            return trackedDownload;
        }

        private TrackedDownload GetTrackedDownload(int queueId)
        {
            var queueItem = _queueService.Find(queueId);

            if (queueItem == null)
            {
                throw new NotFoundException();
            }

            var trackedDownload = _trackedDownloadService.Find(queueItem.DownloadId);

            if (trackedDownload == null)
            {
                throw new NotFoundException();
            }

            return trackedDownload;
        }

        private QueueResource MapToResource(NzbDrone.Core.Queue.Queue queueItem, bool includeAuthor, bool includeBook)
        {
            return queueItem.ToResource(includeAuthor, includeBook);
        }

        [NonAction]
        public void Handle(QueueUpdatedEvent message)
        {
            BroadcastResourceChange(ModelAction.Sync);
        }

        [NonAction]
        public void Handle(PendingReleasesUpdatedEvent message)
        {
            BroadcastResourceChange(ModelAction.Sync);
        }
    }
}
