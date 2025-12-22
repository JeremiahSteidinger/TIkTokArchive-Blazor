using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TikTokArchive.Entities.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1: Drop the foreign key constraint from VideoTags to Videos
            migrationBuilder.DropForeignKey(
                name: "FK_VideoTags_Videos_VideoId",
                table: "VideoTags");

            // Step 2: Drop the index
            migrationBuilder.DropIndex(
                name: "IX_VideoTags_VideoId",
                table: "VideoTags");

            // Step 3: Drop the Tag column
            migrationBuilder.DropColumn(
                name: "Tag",
                table: "VideoTags");

            // Step 4: Add TagId column
            migrationBuilder.AddColumn<int>(
                name: "TagId",
                table: "VideoTags",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Step 5: Create Tags table
            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            // Step 6: Create indexes
            migrationBuilder.CreateIndex(
                name: "IX_VideoTags_TagId",
                table: "VideoTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_VideoTags_VideoId_TagId",
                table: "VideoTags",
                columns: new[] { "VideoId", "TagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                table: "Tags",
                column: "Name",
                unique: true);

            // Step 7: Recreate foreign key to Videos and add new foreign key to Tags
            migrationBuilder.AddForeignKey(
                name: "FK_VideoTags_Videos_VideoId",
                table: "VideoTags",
                column: "VideoId",
                principalTable: "Videos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_VideoTags_Tags_TagId",
                table: "VideoTags",
                column: "TagId",
                principalTable: "Tags",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VideoTags_Tags_TagId",
                table: "VideoTags");

            migrationBuilder.DropForeignKey(
                name: "FK_VideoTags_Videos_VideoId",
                table: "VideoTags");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropIndex(
                name: "IX_VideoTags_TagId",
                table: "VideoTags");

            migrationBuilder.DropIndex(
                name: "IX_VideoTags_VideoId_TagId",
                table: "VideoTags");

            migrationBuilder.DropColumn(
                name: "TagId",
                table: "VideoTags");

            migrationBuilder.AddColumn<string>(
                name: "Tag",
                table: "VideoTags",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_VideoTags_VideoId",
                table: "VideoTags",
                column: "VideoId");

            migrationBuilder.AddForeignKey(
                name: "FK_VideoTags_Videos_VideoId",
                table: "VideoTags",
                column: "VideoId",
                principalTable: "Videos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
