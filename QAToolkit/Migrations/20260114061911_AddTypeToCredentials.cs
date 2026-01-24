using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QAToolkit.Migrations
{
    /// <inheritdoc />
    public partial class AddTypeToCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Remove the old Name and Role columns from Credentials table
            migrationBuilder.DropColumn(
                name: "Name",
                table: "Credentials");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Credentials");

            // Add the new Type column
            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Credentials",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the changes
            migrationBuilder.DropColumn(
                name: "Type",
                table: "Credentials");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Credentials",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Credentials",
                type: "TEXT",
                nullable: true);
        }
    }
}
