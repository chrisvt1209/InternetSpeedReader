using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThroughputMonitor.Core.Migrations;

/// <inheritdoc />
public partial class _20261118221619_InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Adapters",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: false),
                LinkSpeedBps = table.Column<long>(type: "INTEGER", nullable: false),
                IsMonitored = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Adapters", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ProcessSamples",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SessionId = table.Column<long>(type: "INTEGER", nullable: true),
                TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ProcessName = table.Column<string>(type: "TEXT", nullable: false),
                ExePath = table.Column<string>(type: "TEXT", nullable: true),
                Pid = table.Column<int>(type: "INTEGER", nullable: false),
                RxBytesPerSec = table.Column<double>(type: "REAL", nullable: false),
                TxBytesPerSec = table.Column<double>(type: "REAL", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProcessSamples", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Rollups",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                AdapterId = table.Column<int>(type: "INTEGER", nullable: false),
                BucketStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                BucketSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                RxMin = table.Column<double>(type: "REAL", nullable: false),
                RxAvg = table.Column<double>(type: "REAL", nullable: false),
                RxMax = table.Column<double>(type: "REAL", nullable: false),
                RxP95 = table.Column<double>(type: "REAL", nullable: false),
                TxMin = table.Column<double>(type: "REAL", nullable: false),
                TxAvg = table.Column<double>(type: "REAL", nullable: false),
                TxMax = table.Column<double>(type: "REAL", nullable: false),
                TxP95 = table.Column<double>(type: "REAL", nullable: false),
                RxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                TxBytes = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Rollups", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Samples",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SessionId = table.Column<long>(type: "INTEGER", nullable: true),
                AdapterId = table.Column<int>(type: "INTEGER", nullable: false),
                TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                IntervalMs = table.Column<int>(type: "INTEGER", nullable: false),
                RxBytesPerSec = table.Column<double>(type: "REAL", nullable: false),
                TxBytesPerSec = table.Column<double>(type: "REAL", nullable: false),
                LinkSpeedBps = table.Column<long>(type: "INTEGER", nullable: false),
                CpuPercent = table.Column<double>(type: "REAL", nullable: false),
                DiskBytesPerSec = table.Column<double>(type: "REAL", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Samples", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Sessions",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                AdapterId = table.Column<int>(type: "INTEGER", nullable: false),
                StartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                EndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                Label = table.Column<string>(type: "TEXT", nullable: true),
                Category = table.Column<string>(type: "TEXT", nullable: true),
                Note = table.Column<string>(type: "TEXT", nullable: true),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                PeakRxBps = table.Column<double>(type: "REAL", nullable: false),
                PeakTxBps = table.Column<double>(type: "REAL", nullable: false),
                AvgRxBps = table.Column<double>(type: "REAL", nullable: false),
                AvgTxBps = table.Column<double>(type: "REAL", nullable: false),
                TotalRxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                TotalTxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                MinLinkSpeedBps = table.Column<long>(type: "INTEGER", nullable: false),
                AvgCpuPercent = table.Column<double>(type: "REAL", nullable: false),
                PeakCpuPercent = table.Column<double>(type: "REAL", nullable: false),
                AvgDiskBytesPerSec = table.Column<double>(type: "REAL", nullable: false),
                PeakDiskBytesPerSec = table.Column<double>(type: "REAL", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Sessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_Sessions_Adapters_AdapterId",
                    column: x => x.AdapterId,
                    principalTable: "Adapters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "SessionProcessTotals",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SessionId = table.Column<long>(type: "INTEGER", nullable: false),
                ProcessName = table.Column<string>(type: "TEXT", nullable: false),
                ExePath = table.Column<string>(type: "TEXT", nullable: true),
                RxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                TxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                PeakRxBps = table.Column<double>(type: "REAL", nullable: false),
                PeakTxBps = table.Column<double>(type: "REAL", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SessionProcessTotals", x => x.Id);
                table.ForeignKey(
                    name: "FK_SessionProcessTotals_Sessions_SessionId",
                    column: x => x.SessionId,
                    principalTable: "Sessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "SessionRemoteTotals",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SessionId = table.Column<long>(type: "INTEGER", nullable: false),
                ProcessName = table.Column<string>(type: "TEXT", nullable: false),
                RemoteIp = table.Column<string>(type: "TEXT", nullable: false),
                RemotePort = table.Column<int>(type: "INTEGER", nullable: true),
                RemoteHostName = table.Column<string>(type: "TEXT", nullable: true),
                RxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                TxBytes = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SessionRemoteTotals", x => x.Id);
                table.ForeignKey(
                    name: "FK_SessionRemoteTotals_Sessions_SessionId",
                    column: x => x.SessionId,
                    principalTable: "Sessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ProcessSamples_SessionId",
            table: "ProcessSamples",
            column: "SessionId");

        migrationBuilder.CreateIndex(
            name: "IX_ProcessSamples_TimestampUtc",
            table: "ProcessSamples",
            column: "TimestampUtc");

        migrationBuilder.CreateIndex(
            name: "IX_Rollups_AdapterId_BucketSeconds_BucketStartUtc",
            table: "Rollups",
            columns: new[] { "AdapterId", "BucketSeconds", "BucketStartUtc" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Samples_SessionId",
            table: "Samples",
            column: "SessionId");

        migrationBuilder.CreateIndex(
            name: "IX_Samples_TimestampUtc",
            table: "Samples",
            column: "TimestampUtc");

        migrationBuilder.CreateIndex(
            name: "IX_SessionProcessTotals_SessionId",
            table: "SessionProcessTotals",
            column: "SessionId");

        migrationBuilder.CreateIndex(
            name: "IX_SessionRemoteTotals_SessionId",
            table: "SessionRemoteTotals",
            column: "SessionId");

        migrationBuilder.CreateIndex(
            name: "IX_Sessions_AdapterId",
            table: "Sessions",
            column: "AdapterId");

        migrationBuilder.CreateIndex(
            name: "IX_Sessions_StartUtc",
            table: "Sessions",
            column: "StartUtc");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ProcessSamples");

        migrationBuilder.DropTable(
            name: "Rollups");

        migrationBuilder.DropTable(
            name: "Samples");

        migrationBuilder.DropTable(
            name: "SessionProcessTotals");

        migrationBuilder.DropTable(
            name: "SessionRemoteTotals");

        migrationBuilder.DropTable(
            name: "Sessions");

        migrationBuilder.DropTable(
            name: "Adapters");
    }
}
