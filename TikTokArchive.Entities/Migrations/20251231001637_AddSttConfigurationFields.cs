using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TikTokArchive.Entities.Migrations
{
    /// <inheritdoc />
    public partial class AddSttConfigurationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AzureSpeechKey",
                table: "SearchIndexConfigurations",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "AzureSpeechRegion",
                table: "SearchIndexConfigurations",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "OpenAiApiKey",
                table: "SearchIndexConfigurations",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "SttProvider",
                table: "SearchIndexConfigurations",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "WhisperModel",
                table: "SearchIndexConfigurations",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "WhisperUrl",
                table: "SearchIndexConfigurations",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AzureSpeechKey",
                table: "SearchIndexConfigurations");

            migrationBuilder.DropColumn(
                name: "AzureSpeechRegion",
                table: "SearchIndexConfigurations");

            migrationBuilder.DropColumn(
                name: "OpenAiApiKey",
                table: "SearchIndexConfigurations");

            migrationBuilder.DropColumn(
                name: "SttProvider",
                table: "SearchIndexConfigurations");

            migrationBuilder.DropColumn(
                name: "WhisperModel",
                table: "SearchIndexConfigurations");

            migrationBuilder.DropColumn(
                name: "WhisperUrl",
                table: "SearchIndexConfigurations");
        }
    }
}
