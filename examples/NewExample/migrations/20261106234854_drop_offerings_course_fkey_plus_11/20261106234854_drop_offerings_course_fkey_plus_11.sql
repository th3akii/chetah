ALTER TABLE "course_offerings" DROP CONSTRAINT "offerings_course_fkey"

--> statement-breakpoint

ALTER TABLE "course_offerings" DROP CONSTRAINT "offerings_professor_fkey"

--> statement-breakpoint

ALTER TABLE "instructors" DROP CONSTRAINT "professors_department_fkey"

--> statement-breakpoint

ALTER TABLE "instructors" DROP CONSTRAINT "professors_department_id_email_key"

--> statement-breakpoint

DROP INDEX "grades_student_offering_idx"

--> statement-breakpoint

DROP INDEX "students_department_year_idx"

--> statement-breakpoint

ALTER TABLE "instructors" ADD CONSTRAINT "instructors_department_id_first_name_last_name_key" UNIQUE ("department_id", "first_name", "last_name")

--> statement-breakpoint

CREATE INDEX "students_department_year_idx" ON "students" ("department_id", "index_number")

--> statement-breakpoint

CREATE INDEX "students_phone_idx" ON "students" ("phone")

--> statement-breakpoint

ALTER TABLE "instructors" ADD CONSTRAINT "professors_department_fkey" FOREIGN KEY ("department_id") REFERENCES "departments" ("id") ON DELETE CASCADE ON UPDATE CASCADE

--> statement-breakpoint

ALTER TABLE "course_offerings" ADD CONSTRAINT "offerings_course_fkey" FOREIGN KEY ("course_id") REFERENCES "courses" ("id") ON DELETE RESTRICT

--> statement-breakpoint

ALTER TABLE "course_offerings" ADD CONSTRAINT "offerings_professor_fkey" FOREIGN KEY ("professor_id") REFERENCES "instructors" ("id") ON DELETE RESTRICT ON UPDATE CASCADE
