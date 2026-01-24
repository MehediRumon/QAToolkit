using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QAToolkit.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "UrlResources",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "TestNotes",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "ReleaseNotes",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ModifiedAt",
                table: "ReleaseNotes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "ReleaseNotes",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "MeetingNotes",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModifiedBy",
                table: "MeetingNotes",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "Credentials",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "UrlResources");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "TestNotes");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "ReleaseNotes");

            migrationBuilder.DropColumn(
                name: "ModifiedAt",
                table: "ReleaseNotes");

            migrationBuilder.DropColumn(
                name: "ModifiedBy",
                table: "ReleaseNotes");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "MeetingNotes");

            migrationBuilder.DropColumn(
                name: "ModifiedBy",
                table: "MeetingNotes");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "Credentials");
        }
    }
}
