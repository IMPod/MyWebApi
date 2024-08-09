using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Api.Data.DTO;
using Api.Data.DTO.Notification;
using Api.Helpers.Authorize;
using Swashbuckle.AspNetCore.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Solutions.Server.Data.DataModel.Entities;
using Solutions.Server.Data.DataModel.Kinds;
using Solutions.Server.Services.Implements;
using Solutions.Server.Services.Interfaces;

namespace Api.Controllers
{
    [Route("api/v1/Notifications")]
    [ApiController]
    public class NotificationsController : BaseController
    {
        const string _routeUrl = "api/v1/Notifications";
        //private User _user;
        readonly INotificationService _notificationService;
        readonly INotificationUsersService _notificationUsersService;
        readonly IUserService _userService;
        readonly IDirectoriesService _directoriesService;
        readonly IClusterService _clusterService;
        readonly IAuthenticationService _authenticationService;
        readonly ILogger<NotificationsController> _logger;

        public NotificationsController(
            INotificationService notificationService,
            INotificationUsersService serviceNotificationUsers,
            IUserService userService,
            IDirectoriesService dirService,
            IClusterService clusterService,
            IAuthenticationService authenticationService,
            ILogger<NotificationsController> logger) : base(logger)
        {
            _notificationService = notificationService;
            _notificationUsersService = serviceNotificationUsers;
            _userService = userService;
            _directoriesService = dirService;
            _clusterService = clusterService;
            _authenticationService = authenticationService;
            _logger = logger;
        }

        /// <summary>
        /// Retrieves the authenticated user based on the email from the claims.
        /// </summary>
        /// <returns>The authenticated user.</returns>
        private async Task<User> GetAuthenticatedUserAsync()
        {
            var email = User.Claims.GetUserEmail();
            return await _userService.GetUser(email);
        }

        /// <summary>
        /// Handles exceptions and logs the error message.
        /// </summary>
        /// <param name="e">The exception to handle.</param>
        /// <returns>A server error response.</returns>
        private IActionResult HandleException(Exception e)
        {
            _logger.LogError(e, e.Message);
            return ServerError(e);
        }

        #region api/v1/Notifications
        /// <summary>
        /// Retrieves a list of notifications.
        /// </summary>
        /// <param name="page">The page number for pagination. Values below 1 will be set to 1.</param>
        /// <param name="limit">The number of notifications per page. Values below 1 will be set to 10.</param>
        /// <param name="filter">The filter criteria for searching notifications.</param>
        /// <param name="notificationType">The type of notification (1 - system, 2 - news, null - all).</param>
        /// <param name="sort">The sorting criteria.</param>
        /// <param name="dateFrom">The start date for filtering notifications.</param>
        /// <param name="dateTo">The end date for filtering notifications.</param>
        /// <returns>A list of notifications.</returns>
        [HttpGet("")]
        [SwaggerResponse(200, "Success", typeof(BaseResponseDTO<NotificationResponseDTO>))]
        [SwaggerResponse(401, "Unauthorized", typeof(EmptyResult))]
        [SwaggerResponse(500, "Server error", typeof(string))]
        [Authorize(Roles = Role.SystemAdmin)]
        public async Task<IActionResult> GetNotifications(int page = 1, int limit = 10,
            string filter = "", long? notificationType = null, string sort = "", DateTime? dateFrom = null, DateTime? dateTo = null)
        {
            _logger.LogInformation($"Start GetNotifications: page={page}, limit={limit}, filter={filter}, notificationType={notificationType}, sort={sort}, dateFrom={dateFrom}, dateTo={dateTo}");
            try
            {
                page = Math.Max(page, 1);
                limit = Math.Max(limit, 10);

                var user = await GetAuthenticatedUserAsync();
                if (user == null)
                {
                    _logger.LogWarning("Unauthorized access");
                    return Unauthorized();
                }

                var data = await _notificationService.GetNotifications(page - 1, limit, filter, notificationType, dateFrom, dateTo, sort);
                var result = data.Select(x => new NotificationResponseDTO
                {
                    NotificationId = x.Id,
                    Subject = x.Subject,
                    Body = x.Body,
                    SenderId = x.User.Id,
                    NotificationsType = x.NotificationsType.Id,
                    TimeToTurnOff = x.TimeToTurnOff,
                    CreationDateTime = x.CreationDateTime
                }).ToList();

                var totalCount = await _notificationService.GetNotificationsCount(0, null, null, null);
                var totalFilteredCount = (await _notificationService.GetNotifications(0, null, filter, notificationType, dateFrom, dateTo, sort))?.Count() ?? 0;
                var param = notificationType != null ? $"notificationType={notificationType}" : "";
                var response = new BaseResponseDTO<NotificationResponseDTO>(_routeUrl, page, limit, totalFilteredCount, totalCount, sort, filter, param)
                {
                    Data = result
                };

                _logger.LogInformation($"GetNotifications completed successfully: found {result.Count} records");
                return Ok(response);
            }
            catch (KeyNotFoundException e)
            {
                _logger.LogWarning(e, "Records not found");
                return NotFound(e.Message);
            }
            catch (Exception e)
            {
                return HandleException(e);
            }
        }

        /// <summary>
        /// Retrieves the total count of notifications.
        /// </summary>
        /// <param name="dateFrom">The start date for filtering notifications.</param>
        /// <param name="dateTo">The end date for filtering notifications.</param>
        /// <returns>The count of notifications.</returns>
        [HttpGet("Count")]
        [SwaggerResponse(200, "Success", typeof(CountDTO))]
        [SwaggerResponse(401, "Unauthorized", typeof(EmptyResult))]
        [SwaggerResponse(403, "Access denied", typeof(string))]
        [SwaggerResponse(500, "Server error", typeof(string))]
        [Authorize(Roles = Role.SystemAdmin)]
        public async Task<IActionResult> GetNotificationsCount(DateTime? dateFrom = null, DateTime? dateTo = null)
        {
            _logger.LogInformation("Start GetNotificationsCount: dateFrom={dateFrom}, dateTo={dateTo}", dateFrom, dateTo);
            try
            {
                var user = await GetAuthenticatedUserAsync();
                if (user == null)
                {
                    _logger.LogWarning("Unauthorized access");
                    return Unauthorized();
                }
                if (!User.IsInRole(Role.SystemAdmin))
                {
                    _logger.LogWarning("Access denied for user {user}", user.Email);
                    return Forbid();
                }

                var count = await _notificationService.GetNotificationsCount(0, null, null, null, dateFrom, dateTo);
                _logger.LogInformation($"GetNotificationsCount completed successfully: found {count} records");
                return Ok(new CountDTO(count));
            }
            catch (Exception e)
            {
                return HandleException(e);
            }
        }

        /// <summary>
        /// Creates a notification for one user, multiple users, or a cluster.
        /// The notification is created with status id# 2 - Created.
        /// </summary>
        /// <remarks>author IPod</remarks>
        /// <param name="data">DTO containing required fields {
        /// Sender - sender (If the user is not an Admin, it should be the current user);
        /// (Users[ids] and/or ClusterId) - recipients;
        /// Subject - subject of the notification;
        /// Body - body of the notification;
        /// TypeStatus - type of notification (1 - system, 2 - news);
        /// TimeToTurnOff - lifetime of the notification after which it will be turned off;
        /// StateId - state of the record (active)
        /// }</param>
        /// <returns>Returns the id of the created notification on success</returns>
        [HttpPost("")]
        [SwaggerResponse(200, "Success", typeof(NotificationPostRequestDTO))]
        [SwaggerResponse(401, "Unauthorized", typeof(EmptyResult))]
        [SwaggerResponse(403, "Access denied", typeof(string))]
        [SwaggerResponse(404, "No records found", typeof(EmptyResult))]
        [SwaggerResponse(500, "Server error", typeof(string))]
        [Authorize(Roles = Role.SystemAdmin + "," + Role.SystemOperator)]
        public async Task<IActionResult> PostNotification([FromBody] NotificationRequestDTO data)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest();
                }
                _logger.LogDebug($"Create Notification {JsonConvert.SerializeObject(data)}");

                var user = await GetAuthenticatedUserAsync();
                if (user == null)
                {
                    return Unauthorized();
                }

                if (User.IsInRole(Role.SystemAdmin) || User.IsInRole(Role.SystemOperator))
                {
                    if (data.UserIds.Count == 0 && data.ClusterId == 0)
                    {
                        return NotFound("Users and Clusters empty!");
                    }

                    var notification = new Notification
                    {
                        Body = data.Body,
                        Subject = data.Subject,
                        User = user,
                        TimeToTurnOff = data.TimeToTurnOff,
                        NotificationsType = await _directoriesService.GetNotificationType(data.NotificationsType)
                    };

                    await _notificationService.Create(notification);

                    var users = (await _userService.GetAllUsers()).Where(x => data.UserIds.Contains(x.Id));
                    foreach (var recipient in users)
                    {
                        var notificationUser = new NotificationUsers
                        {
                            Notification = notification,
                            User = recipient,
                            CreatedByUserId = _authenticationService.GetCurrentUserId(),
                            NotificationsStatus = await _directoriesService.GetNotificationStatus((int)NotificationStatus.Created)
                        };
                        await _notificationUsersService.Create(notificationUser);
                    }

                    if (data.ClusterId != 0)
                    {
                        await createNotificationClusterAsync(data, notification);
                    }

                    _logger.LogDebug($"Notification created with Id {notification.Id}");
                    return Ok(new NotificationPostRequestDTO { NotificationsId = notification.Id });
                }
                return Forbid();
            }
            catch (KeyNotFoundException e)
            {
                _logger.LogDebug("Records not found", e);
                return NotFound(e.Message);
            }
            catch (Exception e)
            {
                return HandleException(e);
            }
        }

        /// <summary>
        /// Creates a notification for a cluster.
        /// </summary>
        /// <param name="data">The DTO containing the cluster information and notification details.</param>
        /// <param name="notification">The notification to be created.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task createNotificationClusterAsync(NotificationRequestDTO data, Notification notification)
        {
            var cluster = await _clusterService.GetClusterUsers(data.ClusterId);
            if (cluster == null)
            {
                throw new KeyNotFoundException($"Record ClusterId #{data.ClusterId} not found");
            }

            var departmentUsers = cluster.Departments.Select(d => d.UsersDepartments).ToList();
            foreach (var users in departmentUsers)
            {
                foreach (var user in users)
                {
                    var notificationUser = new NotificationUsers
                    {
                        User = user.User,
                        Notification = notification
                    };

                    await _notificationUsersService.Create(notificationUser);
                }
            }
        }
        #endregion

        #region User
        /// <summary>
        /// Returns all notifications for a user.
        /// </summary>
        /// <remarks>author IPod</remarks>
        /// <param name="userId">The ID of the user whose notifications are being checked.</param>
        /// <param name="page">Any value below 1 will be set to 1. The page number for pagination.</param>
        /// <param name="limit">Any value below 1 will be set to 10. The number of notifications per page.</param>
        /// <param name="filter">The filter criteria.</param>
        /// <param name="dateFrom">The start date for filtering notifications.</param>
        /// <param name="dateTo">The end date for filtering notifications.</param>
        /// <param name="notificationType">The type of notification (1 - system, 2 - news, null - returns all).</param>
        /// <param name="notificationsStatus">The status of the notifications ("Unknown" - 1, "Created" - 2, "Sent" - 3, "Read" - 4, "Disabled" - 5, null - returns all).</param>
        /// <param name="sort">Sorting criteria (created_on, created_on|desc, state, state|desc, status, status|desc, subject, subject|desc, id|desc, default sort by id|asc).</param>
        /// <returns>A list of notifications for the user.</returns>
        [HttpGet("User/{userId}")]
        [SwaggerResponse(200, "Success", typeof(BaseResponseDTO<NotificationResponseDTO>))]
        [SwaggerResponse(401, "Unauthorized", typeof(EmptyResult))]
        [SwaggerResponse(403, "Access denied", typeof(string))]
        [SwaggerResponse(500, "Server error", typeof(string))]
        [Authorize]
        public async Task<IActionResult> GetAllNotificationsUser(long userId, int page = 1, int limit = 10, string filter = "",
            long? notificationType = null, long? notificationsStatus = null, string sort = default, DateTime? dateFrom = null, DateTime? dateTo = null)
        {
            _logger.LogDebug($"Start GetAllNotificationsUser: userId={userId}, page={page}, limit={limit}, filter={filter}, sort={sort}, dateFrom={dateFrom}, dateTo={dateTo}");
            try
            {
                page = Math.Max(page, 1);
                limit = Math.Max(limit, 10);

                var user = await GetAuthenticatedUserAsync();
                if (user == null)
                {
                    _logger.LogWarning("Unauthorized access");
                    return Unauthorized();
                }

                if (!User.IsInRole(Role.SystemAdmin) && userId != user.Id)
                {
                    _logger.LogWarning($"Access denied for user {user.Email}");
                    return Forbid();
                }

                var result = await _notificationUsersService.GetAllNotificationsUser(userId, page - 1, limit, filter, notificationType, notificationsStatus, dateFrom, dateTo, sort);
                var param = notificationType != null ? $"type={notificationType}" : "";
                var items = result.Result.Select(x => x.Notification != null ? new NotificationResponseDTO
                {
                    NotificationId = x.Notification.Id,
                    Subject = x.Notification.Subject,
                    Body = x.Notification.Body,
                    SenderId = x.Notification.User.Id,
                    NotificationsType = x.Notification.NotificationsType.Id,
                    NotificationsStatus = x.NotificationsStatus.Id,
                    TimeToTurnOff = x.Notification.TimeToTurnOff,
                    CreationDateTime = x.CreationDateTime
                } : null).ToList();

                var response = new BaseResponseDTO<NotificationResponseDTO>($"{_routeUrl}/User", page, limit, result.TotalFilteredCount, result.TotalCount, sort, filter, param)
                {
                    Data = items
                };
                _logger.LogDebug($"GetAllNotificationsUser completed successfully: found {items.Count} records");
                return Ok(response);
            }
            catch (Exception e)
            {
                return HandleException(e);
            }
        }
        /// <summary>
        /// Gets the total count of notifications for a user.
        /// </summary>
        /// <remarks>author IPod</remarks>
        /// <param name="userId">The ID of the user whose notifications count is being requested.</param>
        /// <param name="notificationsStatus">The status of the notifications.</param>
        /// <param name="notificationType">The type of notification.</param>
        /// <param name="dateFrom">The start date for filtering notifications.</param>
        /// <param name="dateTo">The end date for filtering notifications.</param>
        /// <returns>The count of notifications.</returns>
        [HttpGet("User/{userId}/Count")]
        [SwaggerResponse(200, "Success", typeof(CountDTO))]
        [SwaggerResponse(401, "Unauthorized", typeof(EmptyResult))]
        [SwaggerResponse(403, "Access denied", typeof(string))]
        [SwaggerResponse(500, "Server error", typeof(string))]
        [Authorize]
        public async Task<IActionResult> GetNotificationsUserCount(long userId, long? notificationsStatus, long? notificationType, DateTime? dateFrom = null, DateTime? dateTo = null)
        {
            _logger.LogDebug($"Start GetNotificationsUserCount: userId={userId}, notificationsStatus={notificationsStatus}, notificationType={notificationType}, dateFrom={dateFrom}, dateTo={dateTo}");
            try
            {
                var user = await GetAuthenticatedUserAsync();
                if (user == null)
                {
                    _logger.LogWarning("Unauthorized access");
                    return Unauthorized();
                }

                if (!User.IsInRole(Role.SystemAdmin) && userId != user.Id)
                {
                    _logger.LogWarning($"Access denied for user {user.Email}");
                    return Forbid();
                }

                var count = await _notificationUsersService.GetNotificationsUserCount(userId, notificationsStatus, notificationType, dateFrom, dateTo);
                _logger.LogDebug($"GetNotificationsUserCount completed successfully: found {count} records");
                return Ok(new CountDTO(count));
            }
            catch (Exception e)
            {
                return HandleException(e);
            }
        }

        /// <summary>
        /// Marks all notifications as read for the selected user.
        /// </summary>
        /// <param name="userId">The current user ID.</param>
        /// <returns>An empty result on success.</returns>
        [HttpPost("User/{userId}/ReadAll")]
        [SwaggerResponse(200, "Success", typeof(EmptyResult))]
        [SwaggerResponse(400, "Invalid model", typeof(string))]
        [SwaggerResponse(401, "Unauthorized", typeof(EmptyResult))]
        [SwaggerResponse(403, "Access denied", typeof(string))]
        [SwaggerResponse(500, "Server error", typeof(string))]
        [Authorize]
        public async Task<IActionResult> MarkAllNotificationsUserAsRead(long userId)
        {
            _logger.LogDebug($"Start MarkAllNotificationsUserAsRead: userId={userId}");
            try
            {
                var user = await GetAuthenticatedUserAsync();
                if (user == null)
                {
                    _logger.LogWarning("Unauthorized access");
                    return Unauthorized();
                }

                if (!User.IsInRole(Role.SystemAdmin) && userId != user.Id)
                {
                    _logger.LogWarning($"Access denied for user {user.Email}");
                    return Forbid();
                }

                if (!ModelState.IsValid)
                {
                    return BadRequest("Invalid model state");
                }

                await _notificationUsersService.MarkAllNotificationsUser(userId);
                _logger.LogDebug("MarkAllNotificationsUserAsRead completed successfully");
                return Ok();
            }
            catch (Exception e)
            {
                return HandleException(e);
            }
        }
        #endregion

        #region {notificationId}
        /// <summary>
        /// Returns a single notification.
        /// </summary>
        /// <remarks>author IPod</remarks>
        /// <param name="notificationId">The ID of the notification.</param>
        /// <returns>A single NotificationResponseDTO.</returns>
        [HttpGet("{notificationId}")]
        [SwaggerResponse(200, "Success", typeof(NotificationResponseDTO))]
        [SwaggerResponse(401, "Unauthorized", typeof(EmptyResult))]
        [SwaggerResponse(403, "Access denied", typeof(string))]
        [SwaggerResponse(404, "No records found", typeof(EmptyResult))]
        [SwaggerResponse(500, "Server error", typeof(string))]
        [Authorize(Roles = Role.SystemAdmin)]
        public async Task<IActionResult> GetNotification(long notificationId)
        {
            _logger.LogDebug($"Start GetNotification: notificationId={notificationId}");
            try
            {
                var user = await GetAuthenticatedUserAsync();
                if (user == null)
                {
                    _logger.LogWarning("Unauthorized access");
                    return Unauthorized();
                }

                var data = await _notificationService.GetNotification(notificationId);
                if (data == null)
                {
                    _logger.LogWarning($"Notification with id #{notificationId} not found");
                    return NotFound($"Notification with id #{notificationId} not found");
                }

                var result = new NotificationResponseDTO
                {
                    NotificationId = data.Id,
                    Subject = data.Subject,
                    Body = data.Body,
                    SenderId = data.User.Id,
                    NotificationsType = data.NotificationsType.Id,
                    TimeToTurnOff = data.TimeToTurnOff,
                    CreationDateTime = data.CreationDateTime
                };

                _logger.LogDebug($"Get notification by id {notificationId} completed successfully");
                return Ok(result);
            }
            catch (KeyNotFoundException e)
            {
                _logger.LogDebug($"Record notification #{notificationId} not found");
                return NotFound(e.Message);
            }
            catch (Exception e)
            {
                return HandleException(e);
            }
        }

        /// <summary>
        /// Updates a notification.
        /// </summary>
        /// <remarks>author IPod</remarks>
        /// <param name="notificationId">The ID of the notification to update.</param>
        /// <param name="data">DTO containing the required fields for the update {
        /// Sender - sender;  
        /// (Users[ids] and/or ClusterId) - recipients;
        /// Subject - subject of the notification;
        /// Body - body of the notification;
        /// TypeStatus - type of notification (1 - system, 2 - news);
        /// TimeToTurnOff - lifetime of the notification after which it will be turned off;
        /// StateId - state of the record (active)
        /// }</param>
        /// <returns>Returns status 200 on success.</returns>
        [HttpPut("{notificationId}")]
        [SwaggerResponse(200, "Success", typeof(EmptyResult))]
        [SwaggerResponse(401, "Unauthorized", typeof(EmptyResult))]
        [SwaggerResponse(403, "Access denied", typeof(string))]
        [SwaggerResponse(404, "No records found", typeof(EmptyResult))]
        [SwaggerResponse(500, "Server error", typeof(string))]
        [Authorize(Roles = Role.SystemAdmin)]
        public async Task<IActionResult> PutNotification(long notificationId, [FromBody] NotificationRequestDTO data)
        {
            _logger.LogDebug($"Start PutNotification: notificationId={notificationId}, data={JsonConvert.SerializeObject(data)}");
            try
            {
                var user = await GetAuthenticatedUserAsync();
                if (user == null)
                {
                    _logger.LogWarning("Unauthorized access");
                    return Unauthorized();
                }

                if (!User.IsInRole(Role.SystemAdmin) && data.SenderId != user.Id)
                {
                    _logger.LogWarning($"Access denied for user {user.Email}");
                    return Forbid();
                }

                if (!await _notificationService.NotificationExists(notificationId))
                {
                    _logger.LogWarning($"Notification with id #{notificationId} not found");
                    return NotFound();
                }

                var sender = await _userService.GetUser(data.SenderId);
                var notification = await _notificationService.GetNotification(notificationId);

                notification.Body = data.Body;
                notification.Subject = data.Subject;
                notification.User = sender;
                notification.NotificationsType = await _directoriesService.GetNotificationType(data.NotificationsType);

                await _notificationService.Update(notification);
                _logger.LogDebug($"Update notification: notificationId={notificationId} completed successfully");
                return Ok();
            }
            catch (KeyNotFoundException e)
            {
                _logger.LogDebug($"Record notification #{notificationId} not found");
                return NotFound(e.Message);
            }
            catch (Exception e)
            {
                return HandleException(e);
            }
        }

        /// <summary>
        /// Updates the status of a notification.
        /// </summary>
        /// <remarks>author IPod</remarks>
        /// <param name="notificationId">The ID of the notification to update.</param>
        /// <param name="data">DTO containing the notification status {
        /// notificationsStatus - status of the notification (Unknown - 1, Created - 2, Sent - 3, Read - 4, Disabled - 5)
        /// }</param>
        /// <returns>Returns status 200 on success.</returns>
        [HttpPatch("{notificationId}")]
        [SwaggerResponse(200, "Success", typeof(EmptyResult))]
        [SwaggerResponse(401, "Unauthorized", typeof(EmptyResult))]
        [SwaggerResponse(403, "Access denied", typeof(string))]
        [SwaggerResponse(404, "No records found", typeof(EmptyResult))]
        [SwaggerResponse(500, "Server error", typeof(string))]
        [Authorize(Roles = Role.SystemAdmin)]
        public async Task<IActionResult> PatchNotification(long notificationId, [FromBody] NotificationPatchRequestDTO data)
        {
            _logger.LogDebug($"Start PatchNotification: notificationId={notificationId}, data={JsonConvert.SerializeObject(data)}");
            try
            {
                var user = await GetAuthenticatedUserAsync();
                if (user == null)
                {
                    _logger.LogWarning("Unauthorized access");
                    return Unauthorized();
                }

                if (!User.IsInRole(Role.SystemAdmin))
                {
                    return Forbid();
                }

                if (!ModelState.IsValid)
                {
                    return BadRequest("Invalid notification status!");
                }

                var notifications = await _notificationUsersService.GetNotifications(notificationId);
                if (notifications.Count == 0)
                {
                    _logger.LogWarning($"Notification with id #{notificationId} not found");
                    return NotFound();
                }

                foreach (var notification in notifications)
                {
                    notification.NotificationsStatus = await _directoriesService.GetNotificationStatus(data.NotificationsStatus);
                    await _notificationUsersService.Update(notification);
                }

                _logger.LogDebug($"Patch notification #{notificationId} status updated to #{data.NotificationsStatus}");
                return Ok();
            }
            catch (KeyNotFoundException e)
            {
                _logger.LogDebug($"Record notification #{notificationId} not found");
                return NotFound(e.Message);
            }
            catch (Exception e)
            {
                return HandleException(e);
            }
        }

        /// <summary>
        /// Updates the status of a notification for a single user.
        /// </summary>
        /// <remarks>author IPod</remarks>
        /// <param name="notificationId">The ID of the notification to update.</param>
        /// <param name="userId">The ID of the user.</param>
        /// <param name="data">DTO containing the notification status {
        /// notificationsStatus - status of the notification (Unknown - 1, Created - 2, Sent - 3, Read - 4, Disabled - 5)
        /// }</param>
        /// <returns>Returns status 200 on success.</returns>
        [HttpPatch("{notificationId}/User/{userId}")]
        [SwaggerResponse(200, "Success", typeof(EmptyResult))]
        [SwaggerResponse(401, "Unauthorized", typeof(EmptyResult))]
        [SwaggerResponse(403, "Access denied", typeof(string))]
        [SwaggerResponse(404, "No records found", typeof(EmptyResult))]
        [SwaggerResponse(500, "Server error", typeof(string))]
        [Authorize]
        public async Task<IActionResult> PatchUserNotification(long notificationId, long userId, [FromBody] NotificationPatchRequestDTO data)
        {
            _logger.LogDebug($"Start PatchUserNotification: notificationId={notificationId}, userId={userId}, data={JsonConvert.SerializeObject(data)}");
            try
            {
                var user = await GetAuthenticatedUserAsync();
                if (user == null)
                {
                    _logger.LogWarning("Unauthorized access");
                    return Unauthorized();
                }

                if (!User.IsInRole(Role.SystemAdmin) && userId != user.Id)
                {
                    return Forbid();
                }

                if (!ModelState.IsValid)
                {
                    return BadRequest("Invalid notification status!");
                }

                var notifications = await _notificationUsersService.GetNotifications(notificationId, userId);
                if (notifications.Count == 0)
                {
                    _logger.LogWarning($"Notification with id #{notificationId} and user id #{userId} not found");
                    return NotFound();
                }

                foreach (var notification in notifications)
                {
                    notification.NotificationsStatus = await _directoriesService.GetNotificationStatus(data.NotificationsStatus);
                    await _notificationUsersService.Update(notification);
                }

                _logger.LogDebug($"Patch notification #{notificationId} status updated to #{data.NotificationsStatus} for user #{userId}");
                return Ok();
            }
            catch (KeyNotFoundException e)
            {
                _logger.LogDebug($"Record notification #{notificationId} not found");
                return NotFound(e.Message);
            }
            catch (Exception e)
            {
                return HandleException(e);
            }
        }

        /// <summary>
        /// Deletes a notification.
        /// </summary>
        /// <remarks>author IPod</remarks>
        /// <param name="notificationId">The ID of the notification to delete.</param>
        [HttpDelete("{notificationId}")]
        [SwaggerResponse(200, "Success", typeof(EmptyResult))]
        [SwaggerResponse(401, "Unauthorized", typeof(EmptyResult))]
        [SwaggerResponse(403, "Access denied", typeof(string))]
        [SwaggerResponse(404, "No records found", typeof(EmptyResult))]
        [SwaggerResponse(500, "Server error", typeof(string))]
        [Authorize(Roles = Role.SystemAdmin)]
        public async Task<IActionResult> DeleteNotification(long notificationId)
        {
            _logger.LogDebug($"Start DeleteNotification: notificationId={notificationId}");
            try
            {
                var user = await GetAuthenticatedUserAsync();
                if (user == null)
                {
                    _logger.LogWarning("Unauthorized access");
                    return Unauthorized();
                }

                if (!User.IsInRole(Role.SystemAdmin))
                {
                    return Forbid();
                }

                if (!await _notificationService.NotificationExists(notificationId))
                {
                    _logger.LogWarning($"Notification with id #{notificationId} not found");
                    return NotFound();
                }

                await _notificationService.Delete(notificationId, null);
                _logger.LogDebug($"Notification with id #{notificationId} deleted successfully");
                return Ok();
            }
            catch (KeyNotFoundException e)
            {
                _logger.LogDebug($"Record notification #{notificationId} not found");
                return NotFound(e.Message);
            }
            catch (Exception e)
            {
                return HandleException(e);
            }
        }
        #endregion
    }
}
