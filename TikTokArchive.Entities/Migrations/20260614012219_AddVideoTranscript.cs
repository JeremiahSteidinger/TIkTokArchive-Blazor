using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TikTokArchive.Entities.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoTranscript : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Transcript",
                table: "Videos",
                type: "LONGTEXT",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "TranscriptErrorMessage",
                table: "Videos",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "TranscriptLastAttempt",
                table: "Videos",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TranscriptRetryCount",
                table: "Videos",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TranscriptStatus",
                table: "Videos",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Videos_TranscriptStatus_TranscriptLastAttempt",
                table: "Videos",
                columns: new[] { "TranscriptStatus", "TranscriptLastAttempt" });

            // Speech-to-text was switched on for new videos only: exclude the existing
            // back-catalog by marking every current row NotRequested (4). New videos
            // inserted afterward carry the CLR default Pending (0) — EF sends the value
            // explicitly on insert — so the worker only picks up videos added from now on.
            // An admin action can flip these to Pending later to backfill on demand.
            migrationBuilder.Sql("UPDATE Videos SET TranscriptStatus = 4;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Videos_TranscriptStatus_TranscriptLastAttempt",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "Transcript",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "TranscriptErrorMessage",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "TranscriptLastAttempt",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "TranscriptRetryCount",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "TranscriptStatus",
                table: "Videos");
        }
    }
}
