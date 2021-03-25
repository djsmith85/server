﻿using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bit.Core.Context;
using Bit.Core.Enums;
using Bit.Core.Exceptions;
using Bit.Core.Models.Business;
using Bit.Core.Models.Data;
using Bit.Core.Models.Table;
using Bit.Core.Repositories;
using Bit.Core.Settings;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json;

namespace Bit.Core.Services
{
    public class SendService : ISendService
    {
        private readonly ISendRepository _sendRepository;
        private readonly IUserRepository _userRepository;
        private readonly IPolicyRepository _policyRepository;
        private readonly IUserService _userService;
        private readonly IOrganizationRepository _organizationRepository;
        private readonly ISendFileStorageService _sendFileStorageService;
        private readonly IPasswordHasher<User> _passwordHasher;
        private readonly IPushNotificationService _pushService;
        private readonly IReferenceEventService _referenceEventService;
        private readonly GlobalSettings _globalSettings;
        private readonly ICurrentContext _currentContext;

        public SendService(
            ISendRepository sendRepository,
            IUserRepository userRepository,
            IUserService userService,
            IOrganizationRepository organizationRepository,
            ISendFileStorageService sendFileStorageService,
            IPasswordHasher<User> passwordHasher,
            IPushNotificationService pushService,
            IReferenceEventService referenceEventService,
            GlobalSettings globalSettings,
            IPolicyRepository policyRepository,
            ICurrentContext currentContext)
        {
            _sendRepository = sendRepository;
            _userRepository = userRepository;
            _userService = userService;
            _policyRepository = policyRepository;
            _organizationRepository = organizationRepository;
            _sendFileStorageService = sendFileStorageService;
            _passwordHasher = passwordHasher;
            _pushService = pushService;
            _referenceEventService = referenceEventService;
            _globalSettings = globalSettings;
            _currentContext = currentContext;
        }

        public async Task SaveSendAsync(Send send)
        {
            // Make sure user can save Sends
            await ValidateUserCanSaveAsync(send.UserId, send);

            if (send.Id == default(Guid))
            {
                await _sendRepository.CreateAsync(send);
                await _pushService.PushSyncSendCreateAsync(send);
                await RaiseReferenceEventAsync(send, ReferenceEventType.SendCreated);
            }
            else
            {
                send.RevisionDate = DateTime.UtcNow;
                await _sendRepository.UpsertAsync(send);
                await _pushService.PushSyncSendUpdateAsync(send);
            }
        }

        public async Task CreateSendAsync(Send send, SendFileData data, Stream stream, long requestLength)
        {
            if (send.Type != SendType.File)
            {
                throw new BadRequestException("Send is not of type \"file\".");
            }

            if (requestLength < 1)
            {
                throw new BadRequestException("No file data.");
            }

            var storageBytesRemaining = 0L;
            if (send.UserId.HasValue)
            {
                var user = await _userRepository.GetByIdAsync(send.UserId.Value);
                if (!(await _userService.CanAccessPremium(user)))
                {
                    throw new BadRequestException("You must have premium status to use file sends.");
                }

                if (user.Premium)
                {
                    storageBytesRemaining = user.StorageBytesRemaining();
                }
                else
                {
                    // Users that get access to file storage/premium from their organization get the default
                    // 1 GB max storage.
                    storageBytesRemaining = user.StorageBytesRemaining(
                        _globalSettings.SelfHosted ? (short)10240 : (short)1);
                }
            }
            else if (send.OrganizationId.HasValue)
            {
                var org = await _organizationRepository.GetByIdAsync(send.OrganizationId.Value);
                if (!org.MaxStorageGb.HasValue)
                {
                    throw new BadRequestException("This organization cannot use file sends.");
                }

                storageBytesRemaining = org.StorageBytesRemaining();
            }

            if (storageBytesRemaining < requestLength)
            {
                throw new BadRequestException("Not enough storage available.");
            }

            var fileId = Utilities.CoreHelpers.SecureRandomString(32, upper: false, special: false);

            try
            {
                data.Id = fileId;
                send.Data = JsonConvert.SerializeObject(data,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                await SaveSendAsync(send);
                await _sendFileStorageService.UploadNewFileAsync(stream, send, fileId);
                // Need to save length of stream since that isn't available until it is read
                if (stream.Length <= requestLength)
                {
                    data.Size = stream.Length;
                    send.Data = JsonConvert.SerializeObject(data,
                        new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                    await SaveSendAsync(send);
                }
                else
                {
                    await DeleteSendAsync(send);
                    throw new BadRequestException("Content-Length header is smaller than file received.");
                }

            }
            catch
            {
                // Clean up since this is not transactional
                await _sendFileStorageService.DeleteFileAsync(send, fileId);
                throw;
            }
        }

        public async Task DeleteSendAsync(Send send)
        {
            await _sendRepository.DeleteAsync(send);
            if (send.Type == Enums.SendType.File)
            {
                var data = JsonConvert.DeserializeObject<SendFileData>(send.Data);
                await _sendFileStorageService.DeleteFileAsync(send, data.Id);
            }
            await _pushService.PushSyncSendDeleteAsync(send);
        }

        public (bool grant, bool passwordRequiredError, bool passwordInvalidError) SendCanBeAccessed(Send send,
            string password)
        {
            var now = DateTime.UtcNow;
            if (send == null || send.MaxAccessCount.GetValueOrDefault(int.MaxValue) <= send.AccessCount ||
                send.ExpirationDate.GetValueOrDefault(DateTime.MaxValue) < now || send.Disabled ||
                send.DeletionDate < now)
            {
                return (false, false, false);
            }
            if (!string.IsNullOrWhiteSpace(send.Password))
            {
                if (string.IsNullOrWhiteSpace(password))
                {
                    return (false, true, false);
                }
                var passwordResult = _passwordHasher.VerifyHashedPassword(new User(), send.Password, password);
                if (passwordResult == PasswordVerificationResult.SuccessRehashNeeded)
                {
                    send.Password = HashPassword(password);
                }
                if (passwordResult == PasswordVerificationResult.Failed)
                {
                    return (false, false, true);
                }
            }

            return (true, false, false);
        }

        // Response: Send, password required, password invalid
        public async Task<(string, bool, bool)> GetSendFileDownloadUrlAsync(Send send, string fileId, string password)
        {
            if (send.Type != SendType.File)
            {
                throw new BadRequestException("Can only get a download URL for a file type of Send");
            }

            var (grantAccess, passwordRequired, passwordInvalid) = SendCanBeAccessed(send, password);

            if (!grantAccess)
            {
                return (null, passwordRequired, passwordInvalid);
            }

            send.AccessCount++;
            await _sendRepository.ReplaceAsync(send);
            await _pushService.PushSyncSendUpdateAsync(send);
            return (await _sendFileStorageService.GetSendFileDownloadUrlAsync(send, fileId), false, false);
        }

        // Response: Send, password required, password invalid
        public async Task<(Send, bool, bool)> AccessAsync(Guid sendId, string password)
        {
            var send = await _sendRepository.GetByIdAsync(sendId);
            var (grantAccess, passwordRequired, passwordInvalid) = SendCanBeAccessed(send, password);

            if (!grantAccess)
            {
                return (null, passwordRequired, passwordInvalid);
            }

            // TODO: maybe move this to a simple ++ sproc?
            if (send.Type != SendType.File)
            {
                // File sends are incremented during file download
                send.AccessCount++;
            }

            await _sendRepository.ReplaceAsync(send);
            await _pushService.PushSyncSendUpdateAsync(send);
            await RaiseReferenceEventAsync(send, ReferenceEventType.SendAccessed);
            return (send, false, false);
        }

        private async Task RaiseReferenceEventAsync(Send send, ReferenceEventType eventType)
        {
            await _referenceEventService.RaiseEventAsync(new ReferenceEvent
            {
                Id = send.UserId ?? default,
                Type = eventType,
                Source = ReferenceEventSource.User,
                SendType = send.Type,
                MaxAccessCount = send.MaxAccessCount,
                HasPassword = !string.IsNullOrWhiteSpace(send.Password),
            });
        }

        public string HashPassword(string password)
        {
            return _passwordHasher.HashPassword(new User(), password);
        }

        private async Task ValidateUserCanSaveAsync(Guid? userId, Send send)
        {
            if (!userId.HasValue || (!_currentContext.Organizations?.Any() ?? true))
            {
                return;
            }

            var policies = await _policyRepository.GetManyByUserIdAsync(userId.Value);

            if (policies == null)
            {
                return;
            }

            foreach (var policy in policies.Where(p => p.Enabled && p.Type == PolicyType.DisableSend))
            {
                if (!_currentContext.ManagePolicies(policy.OrganizationId))
                {
                    throw new BadRequestException("Due to an Enterprise Policy, you are only able to delete an existing Send.");
                }
            }

            if (send.HideEmail.GetValueOrDefault())
            {
                foreach (var policy in policies.Where(p => p.Enabled && p.Type == PolicyType.SendOptions && !_currentContext.ManagePolicies(p.OrganizationId)))
                {
                    SendOptionsPolicyData policyDataObj = null;
                    if (policy.Data != null)
                    {
                        policyDataObj = JsonConvert.DeserializeObject<SendOptionsPolicyData>(policy.Data);
                    }

                    if (policyDataObj?.DisableHideEmail ?? false)
                    {
                        throw new BadRequestException("Due to an Enterprise Policy, you are not allowed to hide your email address on a Send.");
                    }
                }
            }
        }
    }
}
