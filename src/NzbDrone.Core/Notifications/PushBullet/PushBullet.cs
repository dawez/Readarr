using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.PushBullet
{
    public class PushBullet : NotificationBase<PushBulletSettings>
    {
        private readonly IPushBulletProxy _proxy;

        public PushBullet(IPushBulletProxy proxy)
        {
            _proxy = proxy;
        }

        public override string Name => "Pushbullet";
        public override string Link => "https://www.pushbullet.com/";

        public override void OnGrab(GrabMessage grabMessage)
        {
            _proxy.SendNotification(BOOK_GRABBED_TITLE_BRANDED, grabMessage.Message, Settings);
        }

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            _proxy.SendNotification(BOOK_DOWNLOADED_TITLE_BRANDED, message.Message, Settings);
        }

        public override void OnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
            _proxy.SendNotification(AUTHOR_DELETED_TITLE, deleteMessage.Message, Settings);
        }

        public override void OnBookDelete(BookDeleteMessage deleteMessage)
        {
            _proxy.SendNotification(BOOK_DELETED_TITLE, deleteMessage.Message, Settings);
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            _proxy.SendNotification(BOOK_FILE_DELETED_TITLE, deleteMessage.Message, Settings);
        }

        public override void OnHealthIssue(HealthCheck.HealthCheck healthCheck)
        {
            _proxy.SendNotification(HEALTH_ISSUE_TITLE_BRANDED, healthCheck.Message, Settings);
        }

        public override void OnDownloadFailure(DownloadFailedMessage message)
        {
            _proxy.SendNotification(DOWNLOAD_FAILURE_TITLE_BRANDED, message.Message, Settings);
        }

        public override void OnImportFailure(BookDownloadMessage message)
        {
            _proxy.SendNotification(IMPORT_FAILURE_TITLE_BRANDED, message.Message, Settings);
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            failures.AddIfNotNull(_proxy.Test(Settings));

            return new ValidationResult(failures);
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "getDevices")
            {
                // Return early if there is not an API key
                if (Settings.ApiKey.IsNullOrWhiteSpace())
                {
                    return new
                    {
                        devices = new List<object>()
                    };
                }

                Settings.Validate().Filter("ApiKey").ThrowOnError();
                var devices = _proxy.GetDevices(Settings);

                return new
                {
                    options = devices.Where(d => d.Nickname.IsNotNullOrWhiteSpace())
                                            .OrderBy(d => d.Nickname, StringComparer.InvariantCultureIgnoreCase)
                                            .Select(d => new
                                            {
                                                id = d.Id,
                                                name = d.Nickname
                                            })
                };
            }

            return new { };
        }
    }
}
