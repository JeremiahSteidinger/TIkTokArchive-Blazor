using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TikTokArchive.Entities.Migrations
{
    /// <inheritdoc />
    public partial class AddAiSummaryProviderTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiSummaryModel",
                table: "Videos",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "AiSummaryProvider",
                table: "Videos",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Videos_AiSummaryProvider",
                table: "Videos",
                column: "AiSummaryProvider");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Videos_AiSummaryProvider",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "AiSummaryModel",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "AiSummaryProvider",
                table: "Videos");
        }
    }
}
