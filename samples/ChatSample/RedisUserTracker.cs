﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Redis;
using Microsoft.AspNetCore.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace ChatSample
{
    public class RedisUserTracker<THub> : IUserTracker<THub>, IDisposable
    {
        private readonly string ServerId = $"server:{Guid.NewGuid().ToString("D")}";
        private readonly RedisKey ServerIndexRedisKey = "ServerIndex";
        private readonly RedisKey LastSeenRedisKey;
        private readonly RedisKey UserIndexRedisKey;

        private readonly ConnectionMultiplexer _redisConnection;
        private readonly IDatabase _redisDatabase;
        private readonly ISubscriber _redisSubscriber;

        private const string UserAddedChannelName = "UserAdded";
        private const string UserRemovedChannelName = "UserRemoved";
        private readonly RedisChannel _userAddedChannel;
        private readonly RedisChannel _userRemovedChannel;

        private readonly ILogger _logger;

        private HashSet<string> _serverIds = new HashSet<string>();
        private readonly UserEqualityComparer _userEqualityComparer = new UserEqualityComparer();
        private HashSet<UserDetails> _users;
        private readonly object _lockObj = new object();

        private readonly Timer _timer;
        private readonly TimeSpan ServerInactivityWindow = TimeSpan.FromSeconds(120);

        public event Action<UserDetails[]> UsersJoined;
        public event Action<UserDetails[]> UsersLeft;

        public RedisUserTracker(IOptions<RedisOptions> options, ILoggerFactory loggerFactory)
        {
            LastSeenRedisKey = $"{ServerId}:last-seen";
            UserIndexRedisKey = $"{ServerId}:users";
            _users = new HashSet<UserDetails>(_userEqualityComparer);

            _logger = loggerFactory.CreateLogger<RedisUserTracker<THub>>();
            (_redisConnection, _redisDatabase) = StartRedisConnection(options.Value);

            _timer = new Timer(Scan, this, TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10));

            _logger.LogInformation("Started RedisUserTracker with Id: {0}", ServerId);

            _redisSubscriber = _redisConnection.GetSubscriber();
            _userAddedChannel = new RedisChannel(UserAddedChannelName, RedisChannel.PatternMode.Literal);
            _userRemovedChannel = new RedisChannel(UserRemovedChannelName, RedisChannel.PatternMode.Literal);
            _redisSubscriber.Subscribe(_userAddedChannel, (channel, value) =>
            {
                var user = DeserializerUser(value);
                var userAdded = false;
                lock (_lockObj)
                {
                    userAdded = _users.Add(user);
                }

                if (userAdded)
                {
                    UsersJoined(new[] { user });
                }
            });

            _redisSubscriber.Subscribe(_userRemovedChannel, (channel, value) =>
            {
                var user = DeserializerUser(value);
                var userRemoved = false;
                lock (_lockObj)
                {
                    userRemoved = _users.Remove(user);
                }

                UsersLeft(new[] { user });
            });
        }

        private (ConnectionMultiplexer, IDatabase) StartRedisConnection(RedisOptions options)
        {
            // TODO: handle connection failures
            var redisConnection = ConnectToRedis(options, _logger);
            var redisDatabase = redisConnection.GetDatabase(options.Options.DefaultDatabase.GetValueOrDefault());

            // Register connection
            redisDatabase.SetAdd(ServerIndexRedisKey, ServerId);
            redisDatabase.StringSet(LastSeenRedisKey, DateTimeOffset.UtcNow.Ticks);

            return (redisConnection, redisDatabase);
        }

        private static ConnectionMultiplexer ConnectToRedis(RedisOptions options, ILogger logger)
        {
            var loggerTextWriter = new LoggerTextWriter(logger);
            if (options.Factory != null)
            {
                return options.Factory(loggerTextWriter);
            }

            if (options.Options.EndPoints.Any())
            {
                return ConnectionMultiplexer.Connect(options.Options, loggerTextWriter);
            }

            var configurationOptions = new ConfigurationOptions();
            configurationOptions.EndPoints.Add(IPAddress.Loopback, 0);
            configurationOptions.SetDefaultPorts();

            return ConnectionMultiplexer.Connect(configurationOptions, loggerTextWriter);
        }

        public Task<IEnumerable<UserDetails>> UsersOnline()
        {
            lock(_lockObj)
            {
                return Task.FromResult(_users.ToArray().AsEnumerable());
            }
        }

        public async Task AddUser(Connection connection, UserDetails userDetails)
        {
            var key = GetUserRedisKey(connection);
            var user = SerializeUser(connection);
            // need to await to make sure user is added before we call into the Hub
            await _redisDatabase.StringSetAsync(key, SerializeUser(connection));
            await _redisDatabase.SetAddAsync(UserIndexRedisKey, key);
            _ = _redisSubscriber.PublishAsync(_userAddedChannel, user);
        }

        public async Task RemoveUser(Connection connection)
        {
            await _redisDatabase.SetRemoveAsync(UserIndexRedisKey, connection.ConnectionId);
            if (await _redisDatabase.KeyDeleteAsync(GetUserRedisKey(connection)))
            {
                _ = _redisSubscriber.PublishAsync(_userRemovedChannel, SerializeUser(connection));
            }
        }

        private static string GetUserRedisKey(Connection connection) => $"user:{connection.ConnectionId}";

        private static void Scan(object state)
        {
            _ = ((RedisUserTracker<THub>)state).Scan();
        }

        private async Task Scan()
        {
            try
            {
                _logger.LogDebug("Scanning for presence changes");

                _redisDatabase.StringSet(LastSeenRedisKey, DateTimeOffset.UtcNow.Ticks);
                await RemoveExpiredServers();
                await CheckForServerChanges();

                _logger.LogDebug("Completed scanning for presence changes");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while checking presence changes.");
            }
        }

        private async Task RemoveExpiredServers()
        {
            // remove expired servers from server index
            var expiredServers = await _redisDatabase.ScriptEvaluateAsync(
                @"local expired_servers = { }
                local count = 0
                for _, server_key in pairs(redis.call('smembers', KEYS[1])) do
                    local last_seen = tonumber(redis.call('get', server_key..':last-seen'))
                    if last_seen ~= nil and tonumber(ARGV[1]) - last_seen > tonumber(ARGV[2]) then
                        table.insert(expired_servers, server_key)
                        count = count + 1
                    end
                end

                if count > 0 then
                    redis.call('srem', KEYS[1], unpack(expired_servers))
                end
                return expired_servers",
                new[] { ServerIndexRedisKey },
                new RedisValue[] { DateTimeOffset.UtcNow.Ticks, ServerInactivityWindow.Ticks });

            // remove users
            // TODO: this will have to be atomic with the previous script in case a server rejoins and populates
            // the list of users
            foreach (string expiredServerKey in (RedisValue[])expiredServers)
            {
                await _redisDatabase.ScriptEvaluateAsync(
                    @"local key = KEYS[1]
                    if redis.call('exists', key) == 1 then
                        redis.call('del', unpack(redis.call('smembers', key)))
                    end
                    redis.call('del', key..':last-seen', key..':users')",
                    new RedisKey[] { expiredServerKey });
            }

            if (((RedisValue[])expiredServers).Any())
            {
                _logger.LogInformation("Removed entries for expired servers. {0}",
                    string.Join(",", (RedisValue[])expiredServers));
            }
        }

        private async Task CheckForServerChanges()
        {
            // TODO locks!
            var activeServers = new HashSet<string>((await _redisDatabase.SetMembersAsync(ServerIndexRedisKey)).Select(v=>(string)v));
            if (activeServers.Count != _serverIds.Count || activeServers.Any(i => !_serverIds.Contains(i)))
            {
                _serverIds = activeServers;
                await SynchronizeUsers();
            }
        }

        private async Task SynchronizeUsers()
        {
            var remoteUsersJson = await _redisDatabase.ScriptEvaluateAsync(
                @"local server_keys = { }
                for _, key in pairs(redis.call('smembers', KEYS[1])) do
                    table.insert(server_keys, key.. ':users')
                end
                local user_keys = redis.call('sunion', unpack(server_keys))
                local users = { }
                if next(user_keys) ~= nil then
                    users = redis.call('mget', unpack(user_keys))
                end
                return users
                ", new[] { ServerIndexRedisKey });

            var remoteUsers = new HashSet<UserDetails>(
                ((RedisValue[])remoteUsersJson)
                    .Where(u => u.HasValue)
                    .Select(userJson => DeserializerUser(userJson)), _userEqualityComparer);

            UserDetails[] newUsers, zombieUsers;
            lock (_lockObj)
            {
                newUsers = remoteUsers.Except(_users, _userEqualityComparer).ToArray();
                zombieUsers = _users.Except(remoteUsers, _userEqualityComparer).ToArray();
                _users = remoteUsers;
            }

            if (zombieUsers.Any())
            {
                _logger.LogDebug("Removing zombie users: {0}", string.Join(",", zombieUsers.Select(u => u.ConnectionId)));
                UsersLeft(zombieUsers);
            }

            if (newUsers.Any())
            {
                _logger.LogDebug("Adding new users: {0}", string.Join(",", newUsers.Select(u => u.ConnectionId)));
                UsersJoined(newUsers);
            }
        }

        private static string SerializeUser(Connection connection) =>
            $"{{ \"ConnectionID\": \"{connection.ConnectionId}\", \"Name\": \"{connection.User.Identity.Name}\" }}";

        private static UserDetails DeserializerUser(string userJson) =>
            JsonConvert.DeserializeObject<UserDetails>(userJson);

        public void Dispose()
        {
            _timer.Dispose();
            _redisSubscriber.UnsubscribeAll();
            _redisConnection.Dispose();
        }

        private class UserEqualityComparer : IEqualityComparer<UserDetails>
        {
            public bool Equals(UserDetails u1, UserDetails u2)
            {
                return ReferenceEquals(u1, u2) || u1.ConnectionId == u2.ConnectionId;
            }

            public int GetHashCode(UserDetails u)
            {
                return u.ConnectionId.GetHashCode();
            }
        }

        private class LoggerTextWriter : TextWriter
        {
            private readonly ILogger _logger;

            public LoggerTextWriter(ILogger logger)
            {
                _logger = logger;
            }

            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value)
            {
            }

            public override void WriteLine(string value)
            {
                _logger.LogDebug(value);

            }
        }
    }
}