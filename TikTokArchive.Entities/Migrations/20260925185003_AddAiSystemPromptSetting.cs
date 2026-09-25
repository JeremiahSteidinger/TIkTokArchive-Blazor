using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TikTokArchive.Entities.Migrations
{
    /// <inheritdoc />
    public partial class AddAiSystemPromptSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SystemPrompt",
                table: "AiProviderSettings",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SystemPrompt",
                table: "AiProviderSettings");
        }
    }
}
