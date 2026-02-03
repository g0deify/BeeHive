# BeeHive - MQTT Remote Management

C# MQTT-based remote management tool.

## ⚠️ DISCLAIMER

**FOR EDUCATIONAL PURPOSES ONLY**

This software is provided for educational and research purposes. The author is not responsible for any misuse or illegal use of this software. Users are solely responsible for complying with all applicable laws and regulations in their jurisdiction.

## Overview

BeeHive demonstrates MQTT protocol implementation with a client-server architecture for remote system management.

## Features

### Commands
- `whoami` - Get current user information
- `dir [path]` - List directory contents
- `tasklist` - List running processes
- `systeminfo` - Get system information
- `shell:command` - Execute shell commands (180s timeout)
- `screenshot` - Capture and retrieve screenshot
- `download "path"` - Download file from client (50MB max)
- `upload "src" "dest"` - Upload file to client

### Technical Features
- Command queuing system (sequential execution)
- Heartbeat monitoring (30s timeout detection)
- Automatic MQTT broker failover
- Process timeout handling (kills stuck processes)
- File size limits (50MB)
- Base64 payload encoding

## Architecture

- **Server:** Windows Forms application for multi-client management
- **Client:** Console application with automatic reconnection
- **Protocol:** MQTT over public brokers (HiveMQ, Mosquitto, EMQX)
- **Communication:** Base64-encoded message passing

## Technology Stack

- C# / .NET Framework 4.7.2
- Windows Forms
- MQTT (MQTTnet library)
- Windows Native APIs (P/Invoke)

## Performance

Native Windows API implementation:
- `whoami`: 5ms (100x faster than cmd.exe)
- `dir`: 10ms (30x faster)
- `tasklist`: 50ms (16x faster)

## MQTT Brokers

- broker.hivemq.com:1883 (Primary)
- test.mosquitto.org:1883 (Backup)
- broker.emqx.io:1883 (Backup)

*Free public MQTT brokers for testing and educational use*

## Limitations

- No encryption (Base64 encoding only)
- Windows only
- No authentication
- Requires public MQTT broker

---

Built with C# | 2026
