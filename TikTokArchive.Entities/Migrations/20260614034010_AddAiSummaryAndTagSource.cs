using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TikTokArchive.Entities.Migrations
{
    /// <inheritdoc />
    public partial class AddAiSummaryAndTagSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "VideoTags",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AiSummaryErrorMessage",
                table: "Videos",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "AiSummaryLastAttempt",
                table: "Videos",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AiSummaryRetryCount",
                table: "Videos",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AiSummaryStatus",
                table: "Videos",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "Videos",
                type: "LONGTEXT",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Videos_AiSummaryStatus_AiSummaryLastAttempt",
                table: "Videos",
                columns: new[] { "AiSummaryStatus", "AiSummaryLastAttempt" });

            // AI enrichment was switched on for new videos only: exclude the existing
            // back-catalog by marking every current row NotRequested (4). New videos inserted
            // afterward carry the CLR default Pending (0) — EF sends the value explicitly on
            // insert — so the worker only picks up videos added from now on. An admin backfill
            // can flip these to Pending later. (Existing VideoTags keep Source = TikTok (0) via
            // the column default above.) Mirrors AddVideoTranscript.
            migrationBuilder.Sql("UPDATE Videos SET AiSummaryStatus = 4;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Videos_AiSummaryStatus_AiSummaryLastAttempt",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "VideoTags");

            migrationBuilder.DropColumn(
                name: "AiSummaryErrorMessage",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "AiSummaryLastAttempt",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "AiSummaryRetryCount",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "AiSummaryStatus",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "Videos");
        }
    }
}
