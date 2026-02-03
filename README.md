# BeeHive - MQTT Remote Management

C# MQTT-based remote management tool for educational purposes.

## About

BeeHive is a lightweight remote management system built on the MQTT protocol, demonstrating practical implementation of distributed client-server architecture. The project showcases real-time communication between multiple clients and a central server using public MQTT brokers, featuring command execution, file transfer, and system monitoring capabilities. Built with C# and Windows Forms, it serves as a hands-on example of network programming, asynchronous operations, and Windows API integration.

## ⚠️ DISCLAIMER

**FOR EDUCATIONAL PURPOSES ONLY**

This software is provided for educational and research purposes. The author is not responsible for any misuse or illegal use of this software. Users are solely responsible for complying with all applicable laws and regulations in their jurisdiction.

## Features

### Commands
- `whoami` - Get current user information
- `dir [path]` - List directory contents
- `systeminfo` - Get system information
- `shell command` - Execute shell commands (180s timeout)
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

## MQTT Brokers

- broker.hivemq.com:1883 (Primary)
- test.mosquitto.org:1883 (Backup)
- broker.emqx.io:1883 (Backup)

*Free public MQTT brokers for testing and educational use*

---

Built with C# | 2026
